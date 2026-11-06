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

use std::collections::HashMap;

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

#[derive(Clone, Default)]
pub struct DamageReservations {
    statuses: HashMap<(i32, i64), DamageStatus>,
    holders: HashMap<i64, Reservation>,
}

impl DamageReservations {
    pub fn new() -> Self {
        Self::default()
    }

    /// The status is created lazily, as in GameState.GetShipDamageStatus.
    /// Repeated target lookups update health without affecting in-flight
    /// reservations.
    pub fn status(&mut self, side: i32, target_id: i64, health: i32) -> DamageStatus {
        let entry = self.statuses.entry((side, target_id)).or_insert(DamageStatus {
            health, total_reserved: 0,
        });
        entry.health = health;
        *entry
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
        self.statuses.insert(key, updated);
        self.holders.insert(owner_id, Reservation { side, target_id, amount });
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
    /// Retaining records until reset is intentional for delayed shots.
    pub fn target_health_changed(&mut self, target_id: i64, health: i32) {
        for ((_, id), status) in &mut self.statuses {
            if *id == target_id {
                status.health = health;
            }
        }
    }

    pub fn reset(&mut self) {
        self.holders.clear();
        self.statuses.clear();
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
