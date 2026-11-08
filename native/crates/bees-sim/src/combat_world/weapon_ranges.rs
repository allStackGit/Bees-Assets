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
        let mut counts = StepCounts::default();
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
                self.weapon_range_entering_ids.clear();
                let contacts = &mut weapons.range_contacts[slot];
                contacts.sweep = contacts.sweep.wrapping_add(1);
                if contacts.sweep == 0 {
                    // A stamp wrap must not resurrect a contact from an old sweep.
                    contacts.sweep = 1;
                    for stamp in contacts.membership.values_mut() { *stamp = 0; }
                }
                if active && range.enabled {
                    counts.weapon_range_grid_rebuilds += u64::from(self.physics.circle_ship_contact_candidates(
                        range.center, range.radius, &mut self.weapon_range_candidates));
                    counts.weapon_range_candidates += self.weapon_range_candidates.len() as u64;
                    for &index in &self.weapon_range_candidates {
                        let target = &self.ships[index];
                        let hull = &self.physics.ships()[index];
                        if target.side == side || target.vitals.is_dead() ||
                            !collisions.allows(range.layer, hull.layer) { continue; }
                        counts.weapon_range_exact_tests += 1;
                        if self.physics.ship_primary_overlaps_circle(index, range.center, range.radius) {
                            if contacts.membership.insert(target.entity_id, contacts.sweep).is_none() {
                                self.weapon_range_entering_ids.push(target.entity_id);
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
                self.weapon_range_entering_ids.sort_unstable();
                let enters = self.weapon_range_entering_ids.len() as u64;
                contacts.ships.extend_from_slice(&self.weapon_range_entering_ids);
                if exits > 0 || enters > 0 {
                    contacts.revision = contacts.revision.saturating_add(1);
                }
                counts.weapon_range_enters += enters;
                counts.weapon_range_exits += exits as u64;
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
