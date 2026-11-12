//! Source RangeCollider enemy entries/exits and owning-projectile exits.
//! Runs once after the same physics step used by graphical and headless worlds.
//! Source callback order is not guaranteed; simultaneous native entries use
//! canonical ship insertion order. Continuing contacts retain their old order.

use super::*;

#[derive(Clone, Debug, Default)]
pub(super) struct WeaponRangeContacts {
    ships: Vec<i64>,
    membership: HashMap<i64, u64>,
    sweep: u64,
    revision: u64,
}

impl WeaponRangeContacts {
    pub(super) fn remove(&mut self, ship_id: i64) -> bool {
        if self.membership.remove(&ship_id).is_none() { return false; }
        self.ships.retain(|&id| id != ship_id);
        self.revision = self.revision.saturating_add(1);
        true
    }
    pub(super) fn ids(&self) -> &[i64] { &self.ships }
    pub(super) fn revision(&self) -> u64 { self.revision }
    pub(super) fn contains(&self, ship_id: i64) -> bool { self.membership.contains_key(&ship_id) }
}

/// Borrowed authoritative enemy cache, not a newly sorted geometric preview.
/// Revision changes only on actual entries/exits, for target-queue invalidation.
#[derive(Clone, Copy, Debug)]
pub struct NativeWeaponRangeContacts<'a> {
    pub ship_ids: &'a [i64],
    pub revision: u64,
}

impl CombatWorld {
    pub fn weapon_range_contacts(&self, ship_id: i64, slot: usize)
        -> Result<Option<NativeWeaponRangeContacts<'_>>, CombatWorldError>
    {
        if self.weapon_range(ship_id, slot)?.is_none() { return Ok(None); }
        let contacts = &self.ship_weapons[&ship_id].range_contacts[slot];
        Ok(Some(NativeWeaponRangeContacts { ship_ids: &contacts.ships, revision: contacts.revision }))
    }

    pub(super) fn refresh_weapon_range_contacts(&mut self, collisions: &CollisionMatrix)
        -> StepCounts
    {
        let mut counts = StepCounts { weapon_range_exits: std::mem::take(&mut self.pending_weapon_range_exits), ..StepCounts::default() };
        self.range_visible_map_objects.clear();
        // Active-obstacle physics ticks leave the shared grid bound to
        // obstacles; clear arenas keep its already-built ship population.
        // Resolve the faction-wide MapObject union first, allowing the
        // obstacle query to rebind lazily when necessary, before ship
        // contact queries reuse the one ship grid. This avoids scanning
        // every MapObject for every turret in obstacle-rich battles.
        if !self.map_objects.is_empty() {
            for (&ship_id, weapons) in &self.ship_weapons {
                if self.range_visible_map_objects.len() == self.map_objects.len() { break; }
                let owner_index = self.ship_by_id[&ship_id];
                let body = &self.physics.ships()[owner_index];
                if !body.active || self.ships[owner_index].vitals.is_dead() { continue; }
                let position = body.motion.position;
                let rotation = body.motion.rotation_degrees;
                for mount in weapons.turrets.iter().flatten() {
                    if self.range_visible_map_objects.len() == self.map_objects.len() { break; }
                    let Ok(range) = mount.world_range(position, rotation, weapons.last_hull_rotation)
                        else { continue };
                    if !range.enabled { continue; }
                    counts.weapon_range_map_object_grid_rebuilds += u64::from(
                        self.physics.circle_obstacle_contact_candidates(
                            range.center, range.radius, &mut self.weapon_range_candidates,
                        ),
                    );
                    counts.weapon_range_map_object_candidates += self.weapon_range_candidates.len() as u64;
                    for &index in &self.weapon_range_candidates {
                        let obstacle = &self.physics.obstacles()[index];
                        let id = obstacle.id;
                        if self.map_objects.contains_key(&id) &&
                            !self.range_visible_map_objects.contains(&id) &&
                            collisions.allows(range.layer, obstacle.layer)
                        {
                            counts.weapon_range_map_object_exact_tests += 1;
                            if obstacle.primary_overlaps_circle(range.center, range.radius) {
                                self.range_visible_map_objects.insert(id);
                            }
                        }
                    }
                }
            }
        }
        let mut has_ranges = false;
        for (&ship_id, weapons) in &mut self.ship_weapons {
            let owner_index = self.ship_by_id[&ship_id];
            let body = &self.physics.ships()[owner_index];
            let position = body.motion.position;
            let rotation = body.motion.rotation_degrees;
            let side = self.ships[owner_index].side;
            let active = body.active && !self.ships[owner_index].vitals.is_dead();
            for slot in 0..weapons.turrets.len() {
                let Some(mount) = weapons.turrets[slot].as_ref() else { continue };
                // Schema20/direct legacy setups have no real range trigger.
                // Do not infer one from nominal range or add query overhead.
                let Ok(range) = mount.world_range(position, rotation, weapons.last_hull_rotation)
                    else { continue };
                has_ranges = true;
                refresh_circle_contacts(&mut self.physics, &self.ships, side, active, range,
                    &mut weapons.range_contacts[slot], &mut self.weapon_range_candidates,
                    &mut self.weapon_range_entering_ids, collisions, &mut counts);
            }
        }

        if !has_ranges { return counts; }

        // Each shot knows its actual weapon slot, so this is linear in shots,
        // not weapons x shots. Match collider overlap, not center distance.
        for index in 0..self.projectiles.len() {
            if !self.physics.projectiles()[index].active { continue; }
            let shot = &self.projectiles[index];
            let Some(slot) = shot.source_turret_slot else { continue };
            // Source exceptions never need range-exit tracking.
            if matches!(shot.projectile_type, 13 | 15) { continue; }
            let owner_alive = self.ship(shot.owner.shooter_id).is_some_and(|owner| !owner.vitals.is_dead());
            let range = self.weapon_range(shot.owner.shooter_id, usize::from(slot)).ok().flatten();
            let within = if let Some(range) = range.filter(|range| range.enabled &&
                collisions.allows(range.layer, self.physics.projectiles()[index].layer)) {
                counts.weapon_range_projectile_tests += 1;
                self.physics.projectiles()[index].primary_overlaps_circle(range.center, range.radius)
            } else { false };
            let exited = self.projectiles[index].within_source_range && !within;
            self.projectiles[index].within_source_range = within;
            // RocketExplosion=13 / FireTankExplosion=15 are explicit source
            // exceptions. Death of the weapon's ship must not kill surviving shots.
            if exited && owner_alive {
                self.end_projectile(index);
                counts.weapon_range_projectile_exits += 1;
            }
        }
        counts
    }
}

// Shared exact trigger membership; only newly entering IDs are sorted.
pub(super) fn refresh_circle_contacts(physics: &mut KinematicPhysics, ships: &[CombatShip],
    side: i32, active: bool, range: NativeWeaponRange, contacts: &mut WeaponRangeContacts,
    candidates: &mut Vec<usize>, entering: &mut Vec<i64>, collisions: &CollisionMatrix,
    counts: &mut StepCounts,
) {
    entering.clear();
    contacts.sweep = contacts.sweep.wrapping_add(1);
    if contacts.sweep == 0 {
        // A stamp wrap must not resurrect a contact from an old sweep.
        contacts.sweep = 1;
        for stamp in contacts.membership.values_mut() { *stamp = 0; }
    }
    if active && range.enabled {
        counts.weapon_range_grid_rebuilds += u64::from(physics.circle_ship_contact_candidates(
            range.center, range.radius, candidates));
        counts.weapon_range_candidates += candidates.len() as u64;
        for &index in candidates.iter() {
            let target = &ships[index];
            let hull = &physics.ships()[index];
            if target.side == side || target.vitals.is_dead() ||
                !collisions.allows(range.layer, hull.layer) { continue; }
            counts.weapon_range_exact_tests += 1;
            if physics.ship_primary_overlaps_circle(index, range.center, range.radius) {
                if contacts.membership.insert(target.entity_id, contacts.sweep).is_none() {
                    entering.push(target.entity_id);
                }
            }
        }
    }
    // Stamp existing contacts rather than sorting the whole live
    // range every tick. Sort only new simultaneous entries; preserve
    // the history of every continuing contact.
    let old_len = contacts.ships.len();
    contacts.ships.retain(|id| {
        if contacts.membership.get(id) == Some(&contacts.sweep) { true }
        else { contacts.membership.remove(id); false }
    });
    let exits = old_len - contacts.ships.len();
    entering.sort_unstable();
    let enters = entering.len() as u64;
    contacts.ships.extend_from_slice(entering);
    if exits > 0 || enters > 0 {
        contacts.revision = contacts.revision.saturating_add(1);
    }
    counts.weapon_range_enters += enters;
    counts.weapon_range_exits += exits as u64;
}
