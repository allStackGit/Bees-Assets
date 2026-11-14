//! Source Scout.DropBeacon policy special and native Beacon spawn lifecycle.
//! Sources: Scripts/Entities/Ships/Scout.cs, Beacon.cs,
//! RlOneVsOneTrainingBootstrap.GetAvailableSpawnSlots.
//! Beacon is a real ship in the same combat world, not an observation-only
//! token. The graphical Level owns the minion Squad and presentation adapter.

use super::*;
use crate::ship_assets::BEACON_TYPE;
use crate::combat::ShipVitals;

/// ConfigData.Gameplay constants. These are source gameplay defaults,
/// independent of rendering or Unity's wall clock.
const COOLDOWN_SECONDS: f32 = 10.0;
const MAX_BEACONS: u8 = 5;
pub const SCOUT_TYPE: u8 = 18;

/// Level-supplied real prefab metadata. ship-colliders.json intentionally
/// has no health or movement stats; never invent those from the hull.
/// A None side cap is ordinary campaign/Free Play semantics.
#[derive(Clone, Debug)]
pub struct ScoutBeaconSpec {
    pub combat_activity: Option<NativeCombatActivity>,
    pub beacon_asset: ShipAsset,
    pub beacon_vitals: ShipVitals,
    pub beacon_base_speed: f32,
    pub beacon_rotation_speed: f32,
    pub beacon_height: f32,
    pub beacon_original_health: i32,
    pub beacon_size_class: f32,
    pub targeting: Option<NativeBeaconTargetSource>,
    pub max_living_ships_per_side: Option<usize>,
}

/// Actual prefab inputs for newly created Beacons, independent of presentation.
#[derive(Clone, Copy, Debug)]
pub struct NativeBeaconTargetSource {
    pub front_offset: f32,
    pub ship_type_letter: u8,
    pub firepower: f32,
}

impl ScoutBeaconSpec {
    fn valid(&self) -> bool {
        self.combat_activity.is_none_or(NativeCombatActivity::valid) &&
            self.beacon_asset.ship_type == BEACON_TYPE &&
            !self.beacon_vitals.is_dead() &&
            self.beacon_original_health > 0 && self.beacon_size_class.is_finite() &&
            self.beacon_size_class >= 0.0 &&
            self.beacon_base_speed.is_finite() &&
            self.beacon_base_speed >= 0.0 &&
            self.beacon_rotation_speed.is_finite() &&
            self.beacon_rotation_speed >= 0.0 &&
            self.beacon_height.is_finite() && self.beacon_height > 0.0 &&
            self.targeting.is_none_or(|source| source.front_offset.is_finite() &&
                source.ship_type_letter < 24 && source.firepower.is_finite() && source.firepower >= 0.0) &&
            self.max_living_ships_per_side != Some(0)
    }
}

#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyScoutRuntime {
    pub(super) dropped: u8,
    pub(super) remaining_cooldown: f32,
    pub(super) enabled: bool,
}

/// A single policy observation can use all three source channels:
/// readiness (action mask), resource fraction, and cooldown/phase.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScoutBeaconStatus {
    pub dropped: u8,
    pub remaining: u8,
    pub cooldown_fraction: f32,
    pub resource_fraction: f32,
    pub phase: f32,
    pub ready: bool,
}

impl CombatWorld {
    /// Bind one Scout to its authored Beacon prefab and persistent fleet ID.
    /// The side cap is optional in ordinary gameplay, required by the
    /// dedicated training adapter. A Scout already has a policy frame.
    pub fn configure_policy_scout(
        &mut self, scout_id: i64, spec: ScoutBeaconSpec,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != SCOUT_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active ||
            !spec.valid() {
            return Err(CombatWorldError::InvalidPolicyScout);
        }
        if !self.fleet_id_by_ship.get(&scout_id).is_some_and(|&id| id != 0) {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        let runtime = self.policy_ship_runtime.get_mut(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.special.is_some() ||
            self.game_scout_runtime.contains_key(&scout_id) ||
            self.scout_beacon_specs.contains_key(&scout_id) {
            return Err(CombatWorldError::InvalidPolicyScout);
        }
        self.scout_beacon_specs.insert(scout_id, spec);
        runtime.special = Some(PolicyShipSpecial::Scout(PolicyScoutRuntime {
            dropped: 0, remaining_cooldown: 0.0, enabled: true,
        }));
        Ok(())
    }

    /// A non-policy gameplay Scout uses exactly the same authored Beacon
    /// prefab/physical spawn as an RL Scout. Its resource clock belongs
    /// to the world, not a replaceable HiveMind Scouting command. The Level
    /// supplies the actual Beacon stats and FleetShip identity at setup.
    pub fn configure_game_scout(
        &mut self, scout_id: i64, spec: ScoutBeaconSpec,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != SCOUT_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active ||
            !self.fleet_id_by_ship.get(&scout_id).is_some_and(|&id| id != 0) ||
            !spec.valid() ||
            self.scout_beacon_specs.contains_key(&scout_id) ||
            self.policy_ship_runtime.contains_key(&scout_id) {
            return Err(CombatWorldError::InvalidPolicyScout);
        }
        self.scout_beacon_specs.insert(scout_id, spec);
        self.game_scout_runtime.insert(scout_id, PolicyScoutRuntime {
            dropped: 0, remaining_cooldown: 0.0, enabled: true,
        });
        Ok(())
    }

    pub fn set_game_scout_enabled(
        &mut self, scout_id: i64, enabled: bool,
    ) -> Result<(), CombatWorldError> {
        let scout = self.game_scout_runtime.get_mut(&scout_id)
            .ok_or(CombatWorldError::InvalidPolicyScout)?;
        scout.enabled = enabled && scout.dropped < MAX_BEACONS;
        Ok(())
    }

    /// An enabled gameplay Scout must be configured with its actual
    /// Beacon prefab at Level setup, independently of command choice.
    pub fn game_scout_configured(&self, scout_id: i64) -> bool {
        self.game_scout_runtime.contains_key(&scout_id)
    }

    /// True when a Scout could still request a Stage-owned Squad ID.
    /// The scheduler must serialize those worlds to preserve the source's
    /// global minion lifetime allocation order across CPU workers.
    ///
    /// A Scout with all five Beacons spent can never spawn another this
    /// episode, even if another callback re-enables it or lowers its
    /// cooldown. This is the one safe dynamic exclusion: temporary
    /// disablement, cooldowns and current side-population caps can change
    /// during a fixed tick and must not permit parallel ID allocation.
    /// Unknown/partially configured entries remain conservative.
    #[inline]
    pub fn has_scout_beacon_sources(&self) -> bool {
        self.scout_beacon_specs.keys().any(|id| {
            if let Some(runtime) = self.game_scout_runtime.get(id) {
                return runtime.dropped < MAX_BEACONS;
            }
            if let Some(runtime) = self.policy_ship_runtime.get(id) {
                if let Some(PolicyShipSpecial::Scout(scout)) = runtime.special {
                    return scout.dropped < MAX_BEACONS;
                }
            }
            true
        })
    }

    /// Read only, suitable for a sparse ten-second Scouting callback.
    pub fn game_scout_beacon_ready(&self, scout_id: i64) -> bool {
        let Some(&runtime) = self.game_scout_runtime.get(&scout_id) else {
            return false;
        };
        if !runtime.enabled || runtime.dropped >= MAX_BEACONS ||
            runtime.remaining_cooldown > 0.0 {
            return false;
        }
        let Some(ship) = self.ship(scout_id) else { return false; };
        if ship.vitals.is_dead() ||
            !self.ship_body(scout_id).is_some_and(|body| body.active) {
            return false;
        }
        self.scout_beacon_specs.get(&scout_id).is_some_and(|spec|
            spec.max_living_ships_per_side.is_none_or(|cap|
                self.living_ships_on_side(ship.side) < cap
            )
        )
    }

    /// Unity Scout.DropBeacon. A rejected attempt does not consume a
    /// charge or start cooldown; the shared physical transaction emits the
    /// same BeaconDropped event used by policy, gameplay and presentation.
    pub fn try_drop_game_scout_beacon(
        &mut self, scout_id: i64,
    ) -> Result<bool, CombatWorldError> {
        if !self.game_scout_beacon_ready(scout_id) {
            return Ok(false);
        }
        if !self.spawn_registered_scout_beacon(scout_id)? {
            return Ok(false);
        }
        let scout = self.game_scout_runtime.get_mut(&scout_id)
            .expect("prevalidated game Scout");
        scout.dropped += 1;
        scout.remaining_cooldown = COOLDOWN_SECONDS;
        if scout.dropped >= MAX_BEACONS { scout.enabled = false; }
        Ok(true)
    }

    /// Player DropBeacon is an input, not an immediate simulation event:
    /// the regular fixed-step event pipeline must record its creation so
    /// the squad/minion registry, reward ledger and group notifications
    /// all observe the new ship exactly once.
    pub fn player_scout_beacon_ready(&self, scout_id: i64) -> bool {
        !self.frozen_after_elimination &&
        !self.policy_ship_runtime.contains_key(&scout_id) &&
        !self.pending_player_scout_beacons.contains(&scout_id) &&
        self.game_scout_beacon_ready(scout_id)
    }

    pub fn queue_player_scout_beacon(
        &mut self, scout_id: i64,
    ) -> Result<bool, CombatWorldError> {
        if self.frozen_after_elimination { return Ok(false); }
        let &index = self.ship_by_id.get(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != SCOUT_TYPE ||
            self.policy_ship_runtime.contains_key(&scout_id) {
            return Err(CombatWorldError::InvalidPolicyScout);
        }
        if !self.player_scout_beacon_ready(scout_id) {
            return Ok(false);
        }
        self.pending_player_scout_beacons.push(scout_id);
        Ok(true)
    }

    pub(super) fn apply_pending_player_scout_beacons(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        for index in 0..self.pending_player_scout_beacons.len() {
            let scout_id = self.pending_player_scout_beacons[index];
            // Another Scout in the same tick can consume the side cap.
            // Revalidate against current world state before the real spawn.
            self.try_drop_game_scout_beacon(scout_id)?;
        }
        self.pending_player_scout_beacons.clear();
        Ok(())
    }

    /// Unity's CanDropBeacons can be disabled independently from a fully
    /// charged but temporarily blocked RL action mask.
    pub fn set_policy_scout_enabled(
        &mut self, scout_id: i64, enabled: bool,
    ) -> Result<(), CombatWorldError> {
        let Some(PolicyShipSpecial::Scout(scout)) = self.policy_ship_runtime
            .get_mut(&scout_id).and_then(|r| r.special.as_mut()) else {
            return Err(CombatWorldError::InvalidPolicyScout);
        };
        scout.enabled = enabled;
        Ok(())
    }

    pub(super) fn policy_scout_ready(
        &self, scout_id: i64, scout: PolicyScoutRuntime,
    ) -> bool {
        if !scout.enabled || scout.dropped >= MAX_BEACONS ||
            scout.remaining_cooldown > 0.0 {
            return false;
        }
        let Some(spec) = self.scout_beacon_specs.get(&scout_id) else {
            return false;
        };
        let Some(ship) = self.ship(scout_id) else { return false };
        spec.max_living_ships_per_side
            .is_none_or(|cap| self.living_ships_on_side(ship.side) < cap)
    }

    pub fn policy_scout_beacon_status(
        &self, scout_id: i64,
    ) -> Option<ScoutBeaconStatus> {
        let PolicyShipSpecial::Scout(scout) =
            self.policy_ship_runtime.get(&scout_id)?.special? else {
            return None;
        };
        let remaining = MAX_BEACONS.saturating_sub(scout.dropped);
        let ready = self.policy_scout_ready(scout_id, scout);
        let cooldown_fraction =
            (scout.remaining_cooldown / COOLDOWN_SECONDS).clamp(0.0, 1.0);
        Some(ScoutBeaconStatus {
            dropped: scout.dropped, remaining,
            cooldown_fraction,
            resource_fraction: f32::from(remaining) / f32::from(MAX_BEACONS),
            phase: if remaining == 0 || !scout.enabled {
                1.0
            } else if ready { 0.0 } else { 0.75 },
            ready,
        })
    }

    /// Each accepted ship-special decision rechecks the live-side cap after
    /// earlier Scouts in the same tick may have already consumed its slots.
    /// A cap denial never consumes a Beacon or starts the cooldown.
    pub(super) fn try_drop_policy_scout_beacon(
        &mut self, scout_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let scout = match self.policy_ship_runtime.get(&scout_id)
            .and_then(|r| r.special) {
            Some(PolicyShipSpecial::Scout(scout)) => scout,
            _ => return Err(CombatWorldError::InvalidPolicyScout),
        };
        if !self.physics.ships()[index].active ||
            self.ships[index].vitals.is_dead() ||
            !scout.enabled || scout.dropped >= MAX_BEACONS ||
            scout.remaining_cooldown > 0.0 {
            return Ok(false);
        }
        if !self.spawn_registered_scout_beacon(scout_id)? {
            self.policy_special_measurements.scout_spawns_blocked_by_cap += 1;
            return Ok(false);
        }
        self.policy_special_measurements.scout_beacons_spawned += 1;

        let Some(PolicyShipSpecial::Scout(scout)) =
            self.policy_ship_runtime.get_mut(&scout_id)
                .and_then(|r| r.special.as_mut()) else {
            unreachable!("prevalidated live Scout");
        };
        scout.dropped += 1;
        scout.remaining_cooldown = COOLDOWN_SECONDS;
        if scout.dropped >= MAX_BEACONS { scout.enabled = false; }
        Ok(true)
    }

    /// Authoritative physical Scout DropBeacon transaction shared by game
    /// commands and RL policy; neither caller owns an independent prefab
    /// spawn, fleet credit, side-cap decision or discovery event.
    fn spawn_registered_scout_beacon(
        &mut self, scout_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&scout_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != SCOUT_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Ok(false);
        }
        let Some(spec) = self.scout_beacon_specs.get(&scout_id) else {
            return Err(CombatWorldError::InvalidPolicyScout);
        };
        let side = self.ships[index].side;
        if spec.max_living_ships_per_side
            .is_some_and(|cap| self.living_ships_on_side(side) >= cap) {
            return Ok(false);
        }
        let spec = spec.clone(); // Arc-backed hull; only copied on actual drop.
        let parent_fleet_id = self.fleet_id_by_ship.get(&scout_id).copied()
            .ok_or(CombatWorldError::InvalidFleetIdentity)?;
        let beacon_id = self.next_entity_id;
        if beacon_id == i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        // Preflight before physical creation: source23 cannot introduce an
        // enemy with missing rank/aim inputs into another weapon's range.
        let targeting = if self.weapon_rank_history.frame.is_some() {
            let source = spec.targeting.ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
            let parent = self.weapon_rank_history.ships.get(&scout_id)
                .ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
            let mut stats = self.weapon_target_rank_stats(scout_id)?;
            stats.ship_id = beacon_id;
            stats.ship_type_letter = source.ship_type_letter;
            stats.last_killed = 0; // Source newborn Ship.ClearData.
            stats.firepower = source.firepower;
            stats.max_range = 0; // Export requires the actual unarmed Beacon prefab.
            stats.speed = spec.beacon_base_speed;
            Some((source.front_offset, NativeWeaponRankSource {
                stats, fleet_owner_ship_id: parent.fleet_owner_ship_id,
            }))
        } else { None };
        let mother_motion = &self.physics.ships()[index].motion;
        let motion = ShipMotion::new(
            // Pool.CreatePooledBeacon uses Quaternion.identity; Setup restores
            // that OriginalRotation. Scout.DropBeacon copies only position.
            mother_motion.position, 0.0,
            spec.beacon_rotation_speed, spec.beacon_base_speed,
            spec.beacon_height,
        );
        self.spawn_ship(
            &spec.beacon_asset, beacon_id, side, spec.beacon_vitals, motion,
        )?;
        if let Some(source) = spec.combat_activity { self.configure_combat_activity(beacon_id, source)?; }
        self.set_ship_fleet_identity(beacon_id, parent_fleet_id)?;
        self.configure_game_ship_original_health(beacon_id, spec.beacon_original_health)?;
        self.set_ship_collision_profile(beacon_id, ShipCollisionProfile {
            original_health: spec.beacon_original_health, size_class: spec.beacon_size_class,
        })?;
        if let Some((front_offset, source)) = targeting {
            self.configure_ship_target_geometry(beacon_id, NativeShipTargetGeometry {
                front_offset, bounds_latched: false,
            })?;
            self.configure_weapon_rank_source(source)?;
        }
        self.beacon_parent_by_id.insert(beacon_id, scout_id);

        self.events.push(CombatEvent::ScoutBeaconDropped {
            scout_id, beacon_id, parent_fleet_id,
        });
        Ok(true)
    }

    /// Scaled game time, not wall time; BTreeMap order is already stable.
    /// The fresh Scout is initially charged exactly as Unity Setup intends.
    pub(super) fn advance_scout_beacon_cooldowns(&mut self, dt: f32) {
        for runtime in self.game_scout_runtime.values_mut() {
            runtime.remaining_cooldown =
                (runtime.remaining_cooldown - dt).max(0.0);
        }
        for runtime in self.policy_ship_runtime.values_mut() {
            let Some(PolicyShipSpecial::Scout(scout)) =
                runtime.special.as_mut() else { continue };
            if scout.remaining_cooldown > 0.0 {
                scout.remaining_cooldown =
                    (scout.remaining_cooldown - dt).max(0.0);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::collision::Aabb;
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::policy::Action;
    use crate::simulation::{World, WorldId};

    #[test]
    fn competing_scouts_enforce_shared_live_side_cap() {
        let ships = ShipAssetCatalog::embedded().unwrap();
        let shots = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut state = CombatWorld::new(Aabb {
            min: Vec2::new(-100.0, -100.0),
            max: Vec2::new(100.0, 100.0),
        }, 8.0, shots).unwrap();
        for id in 1..=2 {
            state.spawn_ship(
                ships.by_type(SCOUT_TYPE).unwrap(), id, 1,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(
                    Vec2::new(id as f32 * 10.0, 0.0),
                    0.0, 0.0, 0.0, 3.0,
                ),
            ).unwrap();
            state.set_ship_fleet_identity(id, 100 + id).unwrap();
            state.bind_policy_ship(id, PolicyFrame::new(0), false).unwrap();
            state.configure_policy_scout(id, ScoutBeaconSpec {
                combat_activity: None,
                beacon_asset: ships.by_type(BEACON_TYPE).unwrap().clone(),
                beacon_vitals: ShipVitals::new(20, 20, 0, 0).unwrap(),
                beacon_base_speed: 0.0,
                beacon_rotation_speed: 0.0,
                beacon_height: 2.0,
                beacon_original_health: 20,
                beacon_size_class: 0.0,
                targeting: None,
                max_living_ships_per_side: Some(3),
            }).unwrap();
            assert!(state.policy_action_mask(id).unwrap().special[1]);
        }
        let mut action = Action::ZERO;
        action.discrete[5] = 1;
        let decisions = [
            Decision { agent_id: 1, action },
            Decision { agent_id: 2, action },
        ];
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows([0; 32]),
        };
        let mut world = World::new(WorldId(71), 22, state);
        rules.step(&mut world, &decisions, 0.02).unwrap();
        assert_eq!(world.state.living_ships_on_side(1), 3);
        assert_eq!(world.state.ships().len(), 3);
        let beacon_id = world.state.events().iter().find_map(|event|
            if let CombatEvent::ScoutBeaconDropped {
                scout_id: 1, beacon_id, parent_fleet_id: 101,
            } = event { Some(*beacon_id) } else { None }
        ).expect("first Scout spawned the one available Beacon");
        assert_eq!(world.state.ship(beacon_id).unwrap().ship_type, BEACON_TYPE);
        assert_eq!(world.state.ship_fleet_identity(beacon_id), Some(101));
        assert_eq!(world.state.scout_beacon_parent(beacon_id), Some(1));
        assert_eq!(world.state.policy_scout_beacon_status(1).unwrap().remaining, 4);
        assert!(!world.state.policy_action_mask(1).unwrap().special[1]);
        assert_eq!(world.state.policy_scout_beacon_status(2).unwrap().remaining, 5);
        assert_eq!(world.state.policy_special_measurements().scout_beacons_spawned, 1);
        assert_eq!(world.state.policy_special_measurements().scout_spawns_blocked_by_cap, 1);
    }
}
