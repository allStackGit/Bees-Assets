//! Zero-copy presentation bridge for the ONE shared native CombatWorld.
//! Rendering is optional and receives borrowed live bodies. Headless PPO
//! never pays to serialize, clone, queue or rebuild presentation state.
//! The renderer owns sprites, cameras and UI; the world owns all collision
//! geometry, health, active projectiles, movement, obstacles and fade timers.

use bees_gameplay::level_events::NativeLevelNotification;
use bees_gameplay::{squads::NativeSquadRoster,
    commands::NativeSquadCommandDirector,
    player_inputs::NativePlayerAuthority};
use bees_sim::combat_world::CombatWorld;
use bees_sim::simulation::WorldId;
use bees_hivemind::session::SquadLifetime;

mod software;
mod selection;
mod minimap;
mod tactical_hud;
mod text;
#[cfg(feature = "client")]
pub(crate) mod campaign_dialogue;
#[cfg(feature = "client")]
mod battle_menu;
#[cfg(feature = "client")]
pub use battle_menu::{BattleMenuRow, select_battle};
pub use software::{NativeCamera, NativeSoftwareRenderer, NativeRenderCounts};
pub use selection::{NativeSquadSelection, NativeSupportMode};
pub use tactical_hud::HudAction;

pub(crate) fn battle_outcome_label(result: bees_sim::episode_terminal::EpisodeTerminalResult) -> String {
    if result.winner_side == 0 { "Draw".to_owned() }
    else if result.extraction_victory { format!("Side {} extracted", result.winner_side) }
    else { format!("Side {} wins", result.winner_side) }
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
    pub campaign_dialogue: Option<(u8, bees_gameplay::campaign_dialogue::CampaignCue)>,
    pub survival: Option<bees_sim::episode_terminal::SurvivalStatus>,
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
    /// One source-scoped player ownership predicate for selection, squad
    /// shortcuts, fog and the minimap. A different side's ships cannot
    /// inherit this viewer's UI access just because another controller
    /// marked its squads user-controlled in the shared Level.
    pub fn is_local_player_squad(&self, owner: SquadLifetime) -> bool {
        let Some(squad) = self.roster.get(owner) else { return false };
        if !self.player_authority.player_permissions(owner)
            .is_some_and(|permission| permission.user_controlled) {
            return false;
        }
        match self.player_authority.player_viewer_side() {
            Some(side) => squad.side == side,
            None => !self.player_authority.player_fog_of_war_enabled(),
        }
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
