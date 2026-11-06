//! World-owned outgoing-damage reservations for Hive Mind target selection.
//!
//! Unity: Weapon.SendProjectile adds to ShipDamageStatus.TotalDamageSentToShip,
//! Projectile.Setup *binds* the existing reservation without increasing it,
//! Projectile.Kill releases it, and Rocket.AddExplosion transfers it first.
//! Bomb.SetTargetShip can reserve before a projectile exists; its weapon holds
//! the reservation until transfer or cancellation.
//!
//! Owners are stable entity/weapon IDs, never pooled object pointers. A dead
//! target's record remains while holders exist so delayed release stays safe.

use std::collections::{hash_map::Entry, HashMap};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReservationError {
    InvalidAmount,
    InvalidSide,
    OwnerAlreadyHolds,
    RecipientAlreadyHolds,
    OwnerMissing,
    TargetUnavailable,
    TotalOverflow,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DamageStatus {
    pub health: i32,
    pub total_reserved: i32,
}

impl DamageStatus {
    pub fn can_select_target(self) -> bool {
        self.health > 0 && self.total_reserved < self.health
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Reservation {
    pub side: i32,
    pub target_id: i64,
    pub amount: i32,
}

/// Cumulative per-world work for syncing target health after real hits.
/// A caller can compare snapshots without retaining per-hit history.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DamageReservationMeasurements {
    pub target_health_changes: u64,
    pub status_rows_refreshed: u64,
}

#[derive(Clone, Default)]
pub struct DamageReservations {
    statuses: HashMap<(i32, i64), DamageStatus>,
    // A hit only changes one target's health. Index its registered sides
    // rather than sweeping every target ever considered by a command.
    status_sides_by_target: HashMap<i64, Vec<i32>>,
    holders: HashMap<i64, Reservation>,
    measurements: DamageReservationMeasurements,
}

impl DamageReservations {
    pub fn new() -> Self {
        Self::default()
    }

    /// The status is created lazily, as in GameState.GetShipDamageStatus.
    /// Repeated target lookups update health without affecting in-flight
    /// reservations.
    pub fn status(&mut self, side: i32, target_id: i64, health: i32) -> DamageStatus {
        let entry = match self.statuses.entry((side, target_id)) {
            Entry::Occupied(entry) => entry.into_mut(),
            Entry::Vacant(entry) => {
                self.status_sides_by_target.entry(target_id)
                    .or_default().push(side);
                entry.insert(DamageStatus { health, total_reserved: 0 })
            }
        };
        entry.health = health;
        *entry
    }

    pub fn measurements(&self) -> DamageReservationMeasurements {
        self.measurements
    }

    pub fn held_by(&self, owner_id: i64) -> Option<Reservation> {
        self.holders.get(&owner_id).copied()
    }

    /// Called ONCE by a weapon's targeting/firing path, before attaching
    /// the reservation to a projectile or fuse. No implicit re-reservation
    /// is performed when a projectile is spawned or hits.
    pub fn reserve(
        &mut self,
        owner_id: i64,
        side: i32,
        target_id: i64,
        health: i32,
        amount: i32,
    ) -> Result<DamageStatus, ReservationError> {
        if side <= 0 {
            return Err(ReservationError::InvalidSide);
        }
        if amount < 0 {
            return Err(ReservationError::InvalidAmount);
        }
        if health <= 0 {
            return Err(ReservationError::TargetUnavailable);
        }
        if self.holders.contains_key(&owner_id) {
            return Err(ReservationError::OwnerAlreadyHolds);
        }
        let key = (side, target_id);
        let old = self.statuses.get(&key).copied().unwrap_or(DamageStatus {
            health, total_reserved: 0,
        });
        let new_total = old.total_reserved.checked_add(amount)
            .ok_or(ReservationError::TotalOverflow)?;
        let updated = DamageStatus { health, total_reserved: new_total };
        if self.statuses.insert(key, updated).is_none() {
            self.status_sides_by_target.entry(target_id)
                .or_default().push(side);
        }
        self.holders.insert(owner_id, Reservation { side, target_id, amount });
        Ok(updated)
    }

    /// Bomb.SetTargetShip replaces a weapon's prior reservation. Check
    /// the complete new total BEFORE releasing the old lease, so failure
    /// cannot erase an existing target while the outgoing command is live.
    /// Successful replacement has the same release-then-add totals as
    /// Unity, including source's saturating release behavior.
    pub fn retarget(
        &mut self, owner_id: i64, side: i32,
        target_id: i64, health: i32, amount: i32,
    ) -> Result<DamageStatus, ReservationError> {
        if side <= 0 {
            return Err(ReservationError::InvalidSide);
        }
        if amount < 0 {
            return Err(ReservationError::InvalidAmount);
        }
        if health <= 0 {
            return Err(ReservationError::TargetUnavailable);
        }
        let prior = self.holders.get(&owner_id).copied();
        let key = (side, target_id);
        let old_status = self.statuses.get(&key).copied()
            .unwrap_or(DamageStatus { health, total_reserved: 0 });
        let same_target = prior.is_some_and(|held|
            (held.side, held.target_id) == key
        );
        let existing_total = if same_target {
            old_status.total_reserved.saturating_sub(
                prior.expect("same_target implies a held lease").amount,
            ).max(0)
        } else {
            old_status.total_reserved
        };
        let new_total = existing_total.checked_add(amount)
            .ok_or(ReservationError::TotalOverflow)?;
        // From here onward every operation is infallible. Unrelated
        // projectile and bomb holders remain in this one global ledger.
        if let Some(old) = prior {
            if !same_target {
                if let Some(status) = self.statuses.get_mut(&(
                    old.side, old.target_id,
                )) {
                    status.total_reserved =
                        status.total_reserved.saturating_sub(old.amount).max(0);
                }
            }
        }
        let updated = DamageStatus { health, total_reserved: new_total };
        if self.statuses.insert(key, updated).is_none() {
            self.status_sides_by_target.entry(target_id)
                .or_default().push(side);
        }
        self.holders.insert(owner_id, Reservation {
            side, target_id, amount,
        });
        Ok(updated)
    }

    /// Rocket explosion or a delayed bomb assumes ownership of precisely
    /// the same existing reservation. Transfer never changes total_reserved.
    pub fn transfer(&mut self, from_id: i64, recipient_id: i64) -> Result<bool, ReservationError> {
        if from_id == recipient_id {
            return Ok(false);
        }
        if self.holders.contains_key(&recipient_id) {
            return Err(ReservationError::RecipientAlreadyHolds);
        }
        let Some(held) = self.holders.remove(&from_id) else {
            return Ok(false);
        };
        self.holders.insert(recipient_id, held);
        Ok(true)
    }

    /// Matches Projectile.RemoveDamageSentEntry: double release is a no-op,
    /// and the aggregate is clamped to zero rather than wrapping negative.
    pub fn release(&mut self, owner_id: i64) -> Option<Reservation> {
        let held = self.holders.remove(&owner_id)?;
        if let Some(status) = self.statuses.get_mut(&(held.side, held.target_id)) {
            status.total_reserved = status.total_reserved.saturating_sub(held.amount).max(0);
        }
        Some(held)
    }

    /// Damage from any side updates the health seen by all target selectors.
    /// Each registered (side,target) status stays until reset for delayed
    /// shots, but a hit need not visit unrelated ships' status records.
    pub fn target_health_changed(&mut self, target_id: i64, health: i32) {
        self.measurements.target_health_changes =
            self.measurements.target_health_changes.saturating_add(1);
        if let Some(sides) = self.status_sides_by_target.get(&target_id) {
            for &side in sides {
                if let Some(status) = self.statuses.get_mut(&(side, target_id)) {
                    status.health = health;
                    self.measurements.status_rows_refreshed =
                        self.measurements.status_rows_refreshed.saturating_add(1);
                }
            }
        }
    }

    pub fn reset(&mut self) {
        self.holders.clear();
        self.statuses.clear();
        self.status_sides_by_target.clear();
        self.measurements = DamageReservationMeasurements::default();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn target_reservation_survives_one_way_continuation_and_releases_once() {
        let mut ledger = DamageReservations::new();
        assert_eq!(ledger.reserve(10, 1, 20, 15, 10).unwrap().total_reserved, 10);
        assert_eq!(ledger.reserve(11, 1, 20, 15, 10).unwrap().total_reserved, 20);
        assert!(!ledger.status(1, 20, 15).can_select_target());
        assert_eq!(ledger.transfer(10, 12), Ok(true));
        assert_eq!(ledger.release(10), None);
        assert_eq!(ledger.status(1, 20, 15).total_reserved, 20);
        assert_eq!(ledger.release(12).unwrap().amount, 10);
        assert_eq!(ledger.release(12), None);
        assert_eq!(ledger.status(1, 20, 15).total_reserved, 10);
        ledger.target_health_changed(20, 0);
        assert!(!ledger.status(1, 20, 0).can_select_target());
        assert_eq!(ledger.release(11).unwrap().amount, 10);
        assert_eq!(ledger.status(1, 20, 0).total_reserved, 0);
    }
}
