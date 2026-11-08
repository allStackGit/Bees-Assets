//! Ordinary ship-targeting passes. The common mounted scheduler remains the
//! sole aim/charge/cooldown/projectile owner; policies never enter this path.

use super::*;
use super::weapons::MountedTurret;
use super::weapon_ranges::WeaponRangeContacts;
use crate::targeting::sort_shooting_targets;

#[derive(Clone, Copy, Debug)]
pub struct NativeAutomaticWeaponInputs {
    pub strategy_id: u8,
    pub has_command: bool,
    /// Real command origin + sequence/outcome, used only for ClearTargets.
    pub command_key: Option<(u8, u64)>,
}

/// Borrowed Level authority: implementations inspect the ONE canonical roster
/// and command director. Neither is cloned or stored by a physical weapon.
pub trait NativeAutomaticWeaponContext {
    fn inputs(&self, ship_id: i64) -> Result<NativeAutomaticWeaponInputs, CombatWorldError>;
    fn prefers(&self, ship_id: i64, target_id: i64) -> bool;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct AutomaticWeaponMeasurements {
    pub target_queries: u64,
    pub queue_builds: u64,
    pub distance_queries: u64,
    pub shots: u64,
}

pub struct NativeAutomaticTurretStatus<'a> {
    pub target_ship: Option<i64>,
    /// Source cached target order. Live values remain in the physical world.
    pub cached_targets: &'a [i64],
    pub targeting_passes: u8,
    pub ready_to_fire: bool,
}

#[derive(Clone, Debug, Default)]
pub(super) struct AutomaticTurret {
    pub(super) target: Option<i64>,
    queue: Vec<i64>,
    cached_range: Option<u64>,
    cached_strategy: u8,
    cached_kill: u64,
    command_key: Option<(u8, u64)>,
}

impl AutomaticTurret {
    pub(super) fn command_changed(&mut self, key: Option<(u8, u64)>) {
        if self.command_key != key {
            if key.is_some() { // source Execute.ClearTargets, not command expiry
                self.target = None;
                self.queue.clear();
                self.cached_range = None;
            }
            self.command_key = key;
        }
    }
}

fn set_target(mount: &mut MountedTurret, target: i64) {
    mount.automatic_state_mut().target = Some(target);
    // LaserBuilder.SetTargetShip activates the same clip for both subclasses.
    if matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder | NativeFirePattern::FullShipTurret) {
        mount.charge_elapsed_seconds.get_or_insert(0.0);
    }
}

impl CombatWorld {
    pub fn automatic_weapon_measurements(&self) -> AutomaticWeaponMeasurements {
        self.automatic_weapon_measurements
    }

    pub fn weapon_automatic_status(&self, ship_id: i64, slot: usize)
        -> Result<Option<NativeAutomaticTurretStatus<'_>>, CombatWorldError>
    {
        if slot >= 5 { return Err(CombatWorldError::InvalidAutomaticTargeting); }
        if self.weapon_rank_history.frame.is_none() || self.policy_ship_runtime.contains_key(&ship_id) { return Ok(None); }
        let Some(mount) = self.ship_weapons.get(&ship_id).and_then(|owner| owner.turrets[slot].as_ref())
            else { return Ok(None) };
        let Some(automatic) = mount.automatic.as_deref() else { return Ok(None) };
        Ok(Some(NativeAutomaticTurretStatus { target_ship: automatic.target,
            cached_targets: &automatic.queue, targeting_passes: mount.state.targeting_passes,
            ready_to_fire: mount.state.ready_to_fire }))
    }

    pub(super) fn automatic_target_valid(&mut self, ship_id: i64,
        contacts: &WeaponRangeContacts, range: NativeWeaponRange, target: i64,
    ) -> Result<bool, CombatWorldError> {
        self.automatic_weapon_measurements.target_queries += 1;
        self.is_ship_target_valid_in_range(ship_id, range, contacts.contains(target), target)
    }

    pub(super) fn automatic_aim_point(&mut self, ship_id: i64, mount: &MountedTurret,
        contacts: &WeaponRangeContacts, range: NativeWeaponRange,
    ) -> Result<Option<Vec2>, CombatWorldError> {
        let Some(target) = mount.automatic_state().target else { return Ok(None) };
        if !self.automatic_target_valid(ship_id, contacts, range, target)? { return Ok(None); }
        Ok(Some(self.target_point_for_range(range, mount.aim_at_front_of_ship
            .ok_or(CombatWorldError::MissingWeaponTargetingGeometry)?, target)?))
    }

    fn choose_automatic_target(&mut self, ship_id: i64, mount: &mut MountedTurret,
        contacts: &WeaponRangeContacts, range: NativeWeaponRange,
    ) -> Result<bool, CombatWorldError> {
        let side = self.ship(ship_id).ok_or(CombatWorldError::UnknownShip)?.side;
        for reserve in [true, false] {
            for index in 0..mount.automatic_state().queue.len() {
                let target = mount.automatic_state().queue[index];
                if self.automatic_target_valid(ship_id, contacts, range, target)? &&
                    (!reserve || self.target_damage_status(side, target)?.can_select_target()) {
                    set_target(mount, target);
                    return Ok(true);
                }
            }
        }
        Ok(false)
    }

    fn try_automatic_fire(&mut self, ship_id: i64, mount: &mut MountedTurret,
        contacts: &WeaponRangeContacts, range: NativeWeaponRange, cease_fire: bool,
    ) -> Result<bool, CombatWorldError> {
        if mount.automatic_state().target.is_none() {
            // Source FireNext uses the existing cached queue, without a sort.
            if !self.choose_automatic_target(ship_id, mount, contacts, range)? { return Ok(false); }
        }
        if cease_fire { return Ok(false); }
        let target = mount.automatic_state().target.expect("chosen source target");
        if !self.automatic_target_valid(ship_id, contacts, range, target)? { return Ok(false); }
        if mount.state.is_aligned_with_target {
            mount.state.ready_to_fire = false;
            return Ok(true);
        }
        // Source GetAimedAtTarget can choose another already aligned candidate.
        // TargetPoint remains the Aim pass's point until the NEXT fixed update.
        for index in 0..mount.automatic_state().queue.len() {
            let candidate = mount.automatic_state().queue[index];
            if !self.automatic_target_valid(ship_id, contacts, range, candidate)? { continue; }
            let point = self.target_point_for_range(range, mount.aim_at_front_of_ship
                .ok_or(CombatWorldError::MissingWeaponTargetingGeometry)?, candidate)?;
            let desired = crate::geometry::heading_towards(point - range.origin);
            if crate::geometry::delta_angle(mount.state.rotation_degrees, desired).abs() <= 3.0 {
                set_target(mount, candidate);
                mount.state.ready_to_fire = false;
                return Ok(true);
            }
        }
        mount.state.ready_to_fire = true;
        Ok(false)
    }

    pub(super) fn automatic_targeting_pass(&mut self, ship_id: i64, mount: &mut MountedTurret,
        contacts: &WeaponRangeContacts, range: NativeWeaponRange,
        inputs: NativeAutomaticWeaponInputs, cease_fire: bool, user_controlled: bool,
        random: &mut RandomStream, context: &dyn NativeAutomaticWeaponContext,
    ) -> Result<bool, CombatWorldError> {
        mount.state.targeting_passes += 1;
        if (mount.state.ready_to_fire && mount.state.is_aligned_with_target) || mount.state.targeting_passes == 3 {
            let fire = self.try_automatic_fire(ship_id, mount, contacts, range, cease_fire)?;
            mount.state.targeting_passes = 0;
            return Ok(fire);
        }
        if cease_fire { return Ok(false); }
        mount.automatic_state_mut().target = None;
        if !(user_controlled || inputs.has_command) { return Ok(false); }
        let kill = self.weapon_rank_history.kill_revisions.get(&ship_id).copied().unwrap_or(0);
        if mount.automatic_state().cached_range != Some(contacts.revision()) ||
            mount.automatic_state().cached_strategy != inputs.strategy_id || mount.automatic_state().cached_kill != kill {
            self.automatic_weapon_measurements.queue_builds += 1;
            // Only IDs persist per weapon. All mounts reuse one world-local
            // ranking/distance scratch, including large multi-weapon fleets.
            let mut candidates = std::mem::take(&mut self.automatic_rank_candidates);
            candidates.clear();
            let preferred = inputs.has_command && contacts.ids().iter().any(|&id| context.prefers(ship_id, id));
            for &id in contacts.ids() {
                if preferred && !context.prefers(ship_id, id) { continue; }
                candidates.push(self.weapon_target_rank_stats(id)?);
            }
            let mut distances = std::mem::take(&mut self.automatic_distance_keys);
            distances.clear();
            if matches!(inputs.strategy_id, 9 | 10) {
                for target in &candidates {
                    let point = self.ship_body(target.ship_id).expect("registered range target")
                        .primary_closest_point_readonly(range.origin);
                    distances.insert(target.ship_id, (point - range.origin).length_squared().sqrt());
                    self.automatic_weapon_measurements.distance_queries += 1;
                }
            }
            sort_shooting_targets(self, inputs.strategy_id, random, &mut candidates, |id| distances[&id])
                .map_err(|_| CombatWorldError::InvalidAutomaticTargeting)?;
            let automatic = mount.automatic_state_mut();
            automatic.queue.clear();
            automatic.queue.extend(candidates.iter().map(|target| target.ship_id));
            automatic.cached_range = Some(contacts.revision());
            automatic.cached_strategy = inputs.strategy_id;
            automatic.cached_kill = kill;
            self.automatic_rank_candidates = candidates;
            self.automatic_distance_keys = distances;
        }
        self.choose_automatic_target(ship_id, mount, contacts, range)?;
        Ok(false)
    }
}
