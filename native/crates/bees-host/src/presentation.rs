//! Zero-copy presentation bridge for the ONE shared native CombatWorld.
//! Rendering is optional and receives borrowed live bodies. Headless PPO
//! never pays to serialize, clone, queue or rebuild presentation state.
//! The renderer owns sprites, cameras and UI; the world owns all collision
//! geometry, health, active projectiles, movement, obstacles and fade timers.

use bees_gameplay::level_events::NativeLevelNotification;
use bees_gameplay::{squads::NativeSquadRoster,
    commands::NativeSquadCommandDirector,
    player_inputs::NativePlayerAuthority};
use bees_sim::collision::Aabb;
use bees_sim::combat_world::CombatWorld;
use bees_sim::simulation::WorldId;
use bees_sim::geometry::Vec2;
use bees_hivemind::session::SquadLifetime;

mod software;
mod selection;
mod minimap;
mod tactical_hud;
mod text;
// Authored mission dialogue also belongs to the headless remote host;
// it contains no window or audio dependency.
pub(crate) mod campaign_dialogue;
pub use campaign_dialogue::{NativeCampaignIntermission, NativeCampaignPreparation};
#[cfg(feature = "client")]
mod controls;
#[cfg(feature = "client")]
pub(crate) use controls::{NativeControls, WorldMoveDrag};
#[cfg(feature = "client")]
pub use controls::show_control_settings;
#[cfg(feature = "client")]
mod codex;
#[cfg(feature = "client")]
mod battle_menu;
#[cfg(feature = "client")]
mod multiplayer_menu;
#[cfg(feature = "client")]
pub use multiplayer_menu::{choose_multiplayer_host, choose_multiplayer_join, MultiplayerJoin};
#[cfg(feature = "client")]
mod remote_window;
#[cfg(feature = "client")]
pub use remote_window::{connect_remote, connect_remote_with_trust};
#[cfg(feature = "client")]
pub(crate) use battle_menu::squad_name_key;
pub use battle_menu::{
    SQUAD_COLORS, BattleMenuRow, NativeBattleMenuAction, NativeShopMenuRow,
    NativeTransferMenuRow, NativeCreateSquadMenuRow, NativeDeleteSquadMenuRow,
    NativeTacticsMenuRow, NativeNameMenuRow, NativeNameTarget,
    NativeCampaignMenuStats, NativeCampaignDebrief, select_battle, select_library,
    show_audio_settings, show_campaign_intermission, show_campaign_debrief,
};
pub use software::{NativeCamera, NativeSoftwareRenderer, NativeRenderCounts};
pub(crate) use software::projectile_splash;
pub use selection::{NativeSquadSelection, NativeSupportMode};
pub use tactical_hud::HudAction;
pub(crate) use tactical_hud::retreat_button_rect;

/// Recognize the released layout independently of the process working directory.
pub(crate) fn packaged_asset_root() -> Option<std::path::PathBuf> {
    let executable = std::env::current_exe().ok()?;
    let directory = executable.parent()?;
    directory.join("examples/native-library.json").is_file()
        .then(|| directory.join("assets"))
}

/// Inverse of the minifb aspect-preserving viewport transform. Share this
/// between native menu and remote-game windows so pointer targeting remains
/// aligned with the existing virtual framebuffer at every desktop size.
#[cfg(feature = "client")]
pub(crate) fn virtual_window_mouse(
    window: &minifb::Window, width: usize, height: usize,
) -> Option<(f32, f32)> {
    use minifb::MouseMode;
    let (actual_width, actual_height) = window.get_size();
    if actual_width == 0 || actual_height == 0 || width == 0 || height == 0 {
        return None;
    }
    let scale = (actual_width as f32 / width as f32)
        .min(actual_height as f32 / height as f32);
    if !scale.is_finite() || scale <= 0.0 { return None; }
    let offset_x = (actual_width as f32 - width as f32 * scale) * 0.5;
    let offset_y = (actual_height as f32 - height as f32 * scale) * 0.5;
    let (x, y) = window.get_mouse_pos(MouseMode::Discard)?;
    let x = (x - offset_x) / scale;
    let y = (y - offset_y) / scale;
    (x >= 0.0 && x < width as f32 && y >= 0.0 && y < height as f32)
        .then_some((x, y))
}

/// Exact original four-stage FireTank sprite selection. Remote players may
/// see the visible smoke/damage stage without receiving undiscovered HP.
pub(crate) fn fire_tank_sprite_stage(health: i32, max_health: i32) -> Option<u8> {
    if max_health <= 0 { return None; }
    let lost = i64::from(max_health) - i64::from(health.max(0));
    Some(((lost * 4) / i64::from(max_health)).clamp(0, 3) as u8)
}


/// Source Prefabs.LoadConversions maps each ship type to one explosion
/// prefab. The prefab AudioSource clips (or lack thereof for Queen) are
/// presentation-only; neither headless simulation nor training infers
/// ship explosions from size or network ownership.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum NativeShipExplosionSound {
    Tiny, Small, Medium, Large, Nuclear, Silent,
}

impl NativeShipExplosionSound {
    pub(crate) fn for_ship_type(kind: u8) -> Self {
        match kind {
            // Tiny Ship Explosion.prefab -> Ship Explosion.mp3
            1 | 8 | 14 | 15 | 18 | 19 | 22 => Self::Tiny,
            // Small Explosion.prefab -> Smaller Explosion.mp3
            12 | 13 | 21 => Self::Small,
            // Medium Explosion.prefab -> Medium Explosion.mp3
            2 | 6 | 7 | 16 => Self::Medium,
            // Large and Huge Explosion.prefabs -> Larger Explosion.mp3
            0 | 3 | 4 | 5 | 9 | 11 | 20 | 23 => Self::Large,
            // Fire Barge Explosion.prefab -> Largest Explosion.mp3
            10 => Self::Nuclear,
            // Queen Explosion.prefab has no AudioSource.
            17 => Self::Silent,
            _ => Self::Silent,
        }
    }

    pub(crate) fn wire_name(self) -> &'static str {
        match self {
            Self::Tiny => "explosion",
            Self::Small => "small_explosion",
            Self::Medium => "medium_explosion",
            Self::Large => "large_explosion",
            Self::Nuclear => "huge_explosion",
            Self::Silent => "silent_explosion",
        }
    }
}

/// Presentation-only FogOfWarVision death mask shared by local and remote
/// viewers. It never affects combat visibility, decisions or simulation.
#[derive(Clone, Copy)]
pub(crate) struct NativeFogFade {
    pub position: Vec2,
    pub radius: f32,
    pub born_tick: u64,
}

impl NativeFogFade {
    pub fn current_radius(self, tick: u64, dt: f32) -> f32 {
        let elapsed = tick.saturating_sub(self.born_tick) as f32 * dt;
        let phases = (elapsed / 0.1).floor().max(0.0);
        self.radius * 0.8f32.powf(phases)
    }
}

/// Source Weapon.Create chooses one clip from each two-sample weapon
/// family ONCE for the mounted weapon, never on every fired projectile.
/// Native clients make that presentation-only choice reproducibly from
/// immutable world/mount identity, without consuming CombatWorld RNG.
/// LAN hosts send only the selected sample bit, never an unseen shooter ID.
#[inline]
pub(crate) fn mounted_weapon_clip_variant(
    ship_id: i64, slot: u8, world: u64, revision: u64,
) -> bool {
    let mut seed = (ship_id as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15)
        ^ (u64::from(slot) + 1).wrapping_mul(0xbf58_476d_1ce4_e5b9)
        ^ world.rotate_left(17) ^ revision.rotate_left(41);
    seed ^= seed >> 30;
    seed = seed.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    seed >> 63 != 0
}

pub(crate) fn battle_outcome_label(result: bees_sim::episode_terminal::EpisodeTerminalResult) -> String {
    if result.winner_side == 0 { "Draw".to_owned() }
    else if result.extraction_victory { format!("Side {} extracted", result.winner_side) }
    else { format!("Side {} wins", result.winner_side) }
}

/// Result causes are derived from the same authored native Level status
/// for local rendering and the authenticated multiplayer wire. No client
/// infers mission completion from hull counts, map visibility or its own clock.
pub(crate) fn battle_outcome_reason(
    view: &NativeRenderWorldView<'_>,
    result: bees_sim::episode_terminal::EpisodeTerminalResult,
) -> &'static str {
    if view.escort.is_some() {
        if result.extraction_victory { "Convoy delivered" } else { "Convoy lost" }
    } else if result.extraction_victory {
        "Extraction"
    } else if view.combat.extraction_retreat_completed() {
        "Fleet withdrew"
    } else if let Some(objective) = view.survival {
        if result.winner_side == objective.defending_side {
            "Defense held"
        } else {
            "Defense failed"
        }
    } else if let Some(objective) = view.target_ship {
        // The named target, not elimination of every enemy unit, is the
        // actual campaign result condition. Use the same authoritative
        // terminal outcome in the local window and LAN receipts.
        if result.winner_side == objective.attacking_side {
            "Mission target destroyed"
        } else {
            "Mission target defended"
        }
    } else if let Some(objective) = view.demolition {
        if result.winner_side == objective.attacking_side {
            "Demolition"
        } else { "Objective defended" }
    } else if result.timed_out {
        "Time limit"
    } else { "Elimination" }
}

/// This view is valid only between fixed steps, and cannot outlive the
/// NativeHostRuntime's immutable borrow. Presentation event effects are
/// acknowledged separately through NativeLevelNotificationSink, not here.
pub struct NativeRenderWorldView<'a> {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub tick: u64,
    pub fixed_step_seconds: f32,
    pub terminal_result: Option<bees_sim::episode_terminal::EpisodeTerminalResult>,
    pub campaign_dialogue: Option<(u32, bees_gameplay::campaign_dialogue::CampaignCue)>,
    /// The original CampaignDialogue player's side, not a mutable local
    /// viewer. Dedicated multiplayer hosts can have no local viewer.
    pub campaign_dialogue_side: Option<i32>,
    pub campaign_camera_ship: Option<i64>,
    pub campaign_scout_alarm: bool,
    pub campaign_gunship_handoff: Option<i64>,
    /// Current source GameState.PlayerScore, read from the same checkpointed
    /// physical death ledger as the terminal campaign result.
    pub campaign_score: Option<(i32, i64)>,
    /// Original progression rights, borrowed from the live source Level.
    pub campaign_codex: Option<(i32, &'a [bool; bees_sim::ship_assets::SHIP_TYPE_COUNT])>,
    /// Source GameState summary (ships lost, safely returned, credited
    /// kills); owned by the same checkpoint as live physical combat.
    pub player_match_counts: Option<(u64, u64, u64)>,
    pub player_loss_types: Option<[u64; bees_sim::ship_assets::SHIP_TYPE_COUNT]>,
    pub player_minerals_mined: Option<u64>,
    /// Independent score of the second genuinely player-controlled side,
    /// never an AI surrogate or a disclosure to the other player.
    pub opponent_score: Option<(i32, i64)>,
    pub opponent_match_counts: Option<(u64, u64, u64)>,
    pub opponent_loss_types: Option<[u64; bees_sim::ship_assets::SHIP_TYPE_COUNT]>,
    pub opponent_minerals_mined: Option<u64>,
    pub campaign_objective: Option<bees_gameplay::campaign_dialogue::CampaignObjectiveKind>,
    /// Source Uranus III rescue identity; never a second ship lifecycle.
    pub rescue_barges: Option<[i64; 3]>,
    pub survival: Option<bees_sim::episode_terminal::SurvivalStatus>,
    pub demolition: Option<bees_sim::episode_terminal::DemolitionStatus>,
    pub target_ship: Option<bees_sim::episode_terminal::TargetShipObjective>,
    pub escort: Option<bees_sim::episode_terminal::EscortStatus>,
    /// Status only: these are read from the Level's source wave queue.
    /// The renderer never schedules, mutates, or owns reinforcements.
    pub reinforcement_waves_pending: usize,
    pub next_reinforcement_tick: Option<u64>,
    /// One authoritative, successfully published arrival. Acknowledging
    /// the durable notification must not extinguish the gameplay banner.
    pub last_reinforcement_tick: Option<u64>,
    pub combat: &'a CombatWorld,
    pub collision_matrix: &'a bees_sim::physics::CollisionMatrix,
    pub roster: &'a NativeSquadRoster,
    /// Same live director as combat weapon targeting and gameplay timers.
    pub commands: &'a NativeSquadCommandDirector,
    pub player_authority: &'a dyn NativePlayerAuthority,
    pub pending_notifications: &'a [NativeLevelNotification],
}

impl<'a> NativeRenderWorldView<'a> {
    pub(crate) fn campaign_camera_subject_for(&self, viewer_side: i32) -> Option<(i64, Vec2)> {
        if self.campaign_dialogue_side != Some(viewer_side) || self.terminal_result.is_some() {
            return None;
        }
        let id = self.campaign_camera_ship?;
        let ship = self.combat.ship(id)?;
        let body = self.combat.ship_body(id)?;
        (ship.side == viewer_side && !ship.vitals.is_dead() && body.active &&
            body.motion.position.is_finite()).then_some((id, body.motion.position))
    }

    /// The source ending reveals the actual battle to its commander
    /// after victory and aims the camera at the authored Bee deployment.
    pub(crate) fn pluto_finale_focus_for(&self, viewer_side: i32) -> Option<Vec2> {
        use bees_gameplay::campaign_dialogue::CampaignCue;

        if !matches!(viewer_side, 1 | 2) || self.campaign_dialogue_side != Some(viewer_side) ||
            !self.terminal_result.is_some_and(|result| result.winner_side == viewer_side) ||
            !self.campaign_dialogue.is_some_and(|(_, cue)| cue == CampaignCue::PlutoAnomalyVictory)
        {
            return None;
        }
        self.player_authority.source_faction_start(3 - viewer_side)
            .filter(|point| point.is_finite())
    }

    pub(crate) fn campaign_gunship_attack_prompt_for(&self, viewer_side: i32) -> Option<i64> {
        use bees_gameplay::campaign_dialogue::CampaignCue;

        if self.campaign_dialogue_side != Some(viewer_side) || self.terminal_result.is_some() ||
            !self.campaign_dialogue.is_some_and(|(_, cue)|
                cue == CampaignCue::PlutoAnomalyGunshipBriefingComplete)
        {
            return None;
        }
        let id = self.campaign_gunship_handoff?;
        (self.combat.ship_body(id).is_some_and(|body| body.active) &&
            self.combat.ship_cease_fire(id) == Some(true)).then_some(id)
    }

    /// The commander receives the tactical panel after Tom's briefing.
    pub(crate) fn campaign_opening_hud_locked_for(&self, viewer_side: i32) -> bool {
        use bees_gameplay::campaign_dialogue::CampaignCue;

        self.campaign_dialogue_side == Some(viewer_side) && self.terminal_result.is_none() &&
            self.campaign_dialogue.is_some_and(|(_, cue)| matches!(cue,
                CampaignCue::PlutoAnomalyBriefing | CampaignCue::PlutoAnomalyHoneybeeSpotted |
                CampaignCue::PlutoAnomalyScoutWithdrawing | CampaignCue::PlutoAnomalyScoutReport |
                CampaignCue::PlutoAnomalyScoutReportComplete | CampaignCue::PlutoAnomalyGunshipArrived |
                CampaignCue::PlutoAnomalyGunshipApproaching | CampaignCue::PlutoAnomalyGunshipContact))
    }

    fn neptune_mining_briefing(&self) -> bool {
        use bees_gameplay::campaign_dialogue::CampaignCue;

        // The latest story cue is retained until another is published.
        // The first physical tick marks the end of the opening briefing
        // in both local play and the authenticated LAN host.
        self.tick == 0 &&
            matches!(self.campaign_dialogue, Some((_, CampaignCue::NeptuneProductionBriefing)))
    }

    pub(crate) fn visible_extraction_objective(&self) -> Option<(i32, Aabb, bool)> {
        use bees_gameplay::campaign_dialogue::CampaignObjectiveKind;

        if self.neptune_mining_briefing() ||
            (self.campaign_objective == Some(CampaignObjectiveKind::TitaniaMinesweeper) &&
                self.combat.active_extraction_bounds().is_none())
        {
            return None;
        }
        self.combat.extraction_objective()
    }

    /// Neptune's briefing explicitly reveals its five mine sites to the
    /// campaign commander. Use their physical positions for the duration
    /// of the cue, including when reopening a checkpoint in the briefing.
    pub(crate) fn briefing_mining_sites(&self, viewer_side: i32)
        -> impl Iterator<Item = Vec2> + '_
    {
        use bees_sim::obstacle::ObstacleKind;

        let visible = self.campaign_dialogue_side == Some(viewer_side) &&
            self.neptune_mining_briefing();
        visible.then_some(()).into_iter()
            .flat_map(move |()| self.combat.render_obstacles())
            .filter_map(|(obstacle, body)| (obstacle.kind == ObstacleKind::MiningAsteroid)
                .then_some(body.pose.position))
    }

    /// Source Beacon.SetBeaconStatus reads ProximityCollider.NearbyEnemyShips.
    /// The trigger is a circle of radius Sight (MaxRange if Sight == 0),
    /// identical to this Level's authored fog vision radius. Check it
    /// against actual current primary hulls, never a separate unit map
    /// or the viewer's possibly hidden ship projection.
    pub fn beacon_has_nearby_enemy(&self, beacon_id: i64) -> bool {
        let Some(beacon) = self.combat.ship(beacon_id) else { return false };
        if beacon.ship_type != bees_sim::ship_assets::BEACON_TYPE ||
            beacon.vitals.is_dead() {
            return false;
        }
        let Some(origin) = self.combat.ship_body(beacon_id)
            .filter(|body| body.active).map(|body| body.motion.position)
        else { return false };
        let Some(radius) = self.player_authority.player_fog_vision_radius(beacon_id)
            .filter(|radius| radius.is_finite() && *radius > 0.0)
        else { return false };
        let radius_squared = radius * radius;
        self.combat.render_ships().any(|(ship, body)| {
            if ship.side == beacon.side { return false; }
            // The existing immutable primary-hull query handles circles,
            // boxes and concave source colliders, including changed poses
            // before the next physics step. Do not inspect a private
            // physics cache or substitute a point-center approximation.
            (body.primary_closest_point_readonly(origin) - origin)
                .length_squared() <= radius_squared
        })
    }

    /// Read only the scoreboard authorized for the requested faction.
    /// Live LAN result receipts call this per authenticated peer.
    pub fn player_match_for_side(&self, side: i32)
        -> Option<(i64, Option<(u64, u64, u64)>)>
    {
        if let Some((owner, score)) = self.campaign_score {
            if owner == side {
                return Some((score, self.player_match_counts));
            }
        }
        self.opponent_score.filter(|(owner, _)| *owner == side)
            .map(|(_, score)| (score, self.opponent_match_counts))
    }

    pub fn minerals_mined_for_side(&self, side: i32) -> Option<u64> {
        if self.campaign_score.is_some_and(|(owner, _)| owner == side) {
            return self.player_minerals_mined;
        }
        self.opponent_score.filter(|(owner, _)| *owner == side)?;
        self.opponent_minerals_mined
    }

    pub fn loss_types_for_side(&self, side: i32)
        -> Option<[u64; bees_sim::ship_assets::SHIP_TYPE_COUNT]>
    {
        if self.campaign_score.is_some_and(|(owner, _)| owner == side) {
            return self.player_loss_types;
        }
        self.opponent_score.filter(|(owner, _)| *owner == side)?;
        self.opponent_loss_types
    }

    /// One source-scoped player ownership predicate for selection, squad
    /// shortcuts, fog and the minimap. A different side's ships cannot
    /// inherit this viewer's UI access just because another controller
    /// marked its squads user-controlled in the shared Level.
    pub fn can_surrender(&self, side: i32) -> bool {
        self.terminal_result.is_none() && self.combat.surrendered_side().is_none() &&
            self.roster.active_lifetimes().any(|owner|
                self.roster.get(owner).is_some_and(|squad| squad.side == side &&
                    self.player_authority.player_permissions(owner)
                        .is_some_and(|p| p.user_controlled && p.can_accept_input))) &&
            (self.campaign_objective != Some(
                bees_gameplay::campaign_dialogue::CampaignObjectiveKind::PlutoAnomaly) ||
                self.campaign_dialogue.is_some_and(|(_, cue)| matches!(cue,
                    bees_gameplay::campaign_dialogue::CampaignCue::PlutoAnomalyGunshipBriefingComplete |
                    bees_gameplay::campaign_dialogue::CampaignCue::PlutoAnomalyHoneybeeDestroyed)))
    }

    pub fn is_local_player_squad(&self, owner: SquadLifetime) -> bool {
        let Some(squad) = self.roster.get(owner) else { return false };
        self.player_authority.player_owns_squad(owner, squad.side)
    }

    /// Read-only current CarrierShip.Carrier for a real source CarrierSquad.
    /// Parent reassignment happens in CombatWorld at Carrier death, so this
    /// never stores a stale presentation parent. Some(None) is an orphan;
    /// None is either a non-Carrier squad or missing/conflicting source data.
    pub fn carrier_squad_parent(&self, owner: SquadLifetime) -> Option<Option<i64>> {
        if self.player_authority.player_carrier_squad(owner) != Some(true) {
            return None;
        }
        let squad = self.roster.get(owner)?;
        let mut shared = None;
        for &id in &squad.members {
            let ship = self.combat.ship(id)?;
            let parent = match ship.ship_type {
                8 => self.combat.drone_carrier(id)?,
                19 => self.combat.game_striker_carrier(id)
                    .map(|carrier| carrier.carrier_id)
                    .or_else(|| self.combat.policy_striker_carrier(id))?,
                _ => return None,
            };
            if let Some(existing) = shared {
                if existing != parent { return None; }
            } else {
                shared = Some(parent);
            }
        }
        shared
    }

    /// Iterate directly over the canonical live bodies. Projectiles,
    /// asteroids and ships all retain their authored collider/gameplay
    /// types; no display-only positional cache or fake interpolator.
    pub fn has_live_ships(&self) -> bool {
        self.combat.render_ships().next().is_some()
    }

    pub fn live_ship_count(&self) -> usize {
        self.combat.total_living_ships()
    }
}
