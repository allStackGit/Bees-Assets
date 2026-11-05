//! Common ship-health and combat-value accounting.
//!
//! Gameplay and headless simulation must call the SAME implementation.
//! Sources: Ship.LogDamage, Ship.LogAttackingDamage and
//! Utilities.CalculateTsv / CalculateHealthFactor on the experimental branch.
//!
//! An applied hit produces data for the rest of the shared game lifecycle.
//! Squad membership, fleet persistence, Hive Mind command credit, RL reward,
//! and terminal conditions are not silently approximated here.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatError {
    InvalidHealth,
    InvalidStartingValue,
    NegativeDamage,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DamageDelta {
    pub health_before: i32,
    pub health_after: i32,
    pub tsv_before: i32,
    pub tsv_after: i32,
    pub applied_damage: i32,
    pub tsv_loss: i32,
    /// Marking the ship as dead is idempotent and belongs to the same
    /// transaction as the final hit. Downstream events must preserve order.
    pub killed: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ShipVitals {
    health: i32,
    max_health: i32,
    max_tsv: i32,
    mined_cargo: i32,
    tsv: i32,
}

impl ShipVitals {
    pub fn new(
        health: i32,
        max_health: i32,
        max_tsv: i32,
        mined_cargo: i32,
    ) -> Result<Self, CombatError> {
        if max_health <= 0 || health < 0 || health > max_health {
            return Err(CombatError::InvalidHealth);
        }
        if max_tsv < 0 || mined_cargo < 0 {
            return Err(CombatError::InvalidStartingValue);
        }
        let mut value = Self {
            health, max_health, max_tsv, mined_cargo, tsv: 0,
        };
        value.tsv = value.calculate_tsv();
        Ok(value)
    }

    pub fn health(&self) -> i32 { self.health }
    pub fn max_health(&self) -> i32 { self.max_health }
    pub fn tsv(&self) -> i32 { self.tsv }
    pub fn is_dead(&self) -> bool { self.health <= 0 }
    pub fn mined_cargo(&self) -> i32 { self.mined_cargo }

    /// Utilities.CalculateHealthFactor:
    /// alive: (MaxHealth + Health) / (MaxHealth * 2), else 0.
    /// C# computes the integer numerator and denominator before the
    /// floating-point division; ordinary authored ship values fit i32.
    pub fn calculate_health_factor(&self) -> f32 {
        if self.health <= 0 {
            return 0.0;
        }
        let numerator = i64::from(self.max_health) + i64::from(self.health);
        let denominator = i64::from(self.max_health) * 2;
        (numerator as f32) / (denominator as f32)
    }

    /// Utilities.CalculateTsv:
    /// trunc(max base TSV * health factor) + cargo while alive.
    /// The cargo is forfeited at death; it must not count as direct
    /// persistent-fleet damage shaping in the RL coordinator.
    pub fn calculate_tsv(&self) -> i32 {
        if self.health <= 0 {
            return 0;
        }
        (self.max_tsv as f32 * self.calculate_health_factor()) as i32
            + self.mined_cargo
    }

    /// The normal and unattributed Ship.LogDamage paths use identical
    /// health and TSV arithmetic; only their attribution differs.
    /// Returns None after death, preserving Unity's early return.
    pub fn apply_damage(
        &mut self,
        power: i32,
        shots_harmless: bool,
    ) -> Result<Option<DamageDelta>, CombatError> {
        if power < 0 {
            return Err(CombatError::NegativeDamage);
        }
        if self.is_dead() {
            return Ok(None);
        }
        let applied_damage = if shots_harmless { 0 } else { power.min(self.health) };
        let before_health = self.health;
        let before_tsv = self.tsv;
        self.health -= applied_damage;
        self.tsv = self.calculate_tsv();
        Ok(Some(DamageDelta {
            health_before: before_health,
            health_after: self.health,
            tsv_before: before_tsv,
            tsv_after: self.tsv,
            applied_damage,
            tsv_loss: before_tsv - self.tsv,
            killed: before_health > 0 && self.health == 0,
        }))
    }

    /// Mining updates the same TValue basis without applying damage.
    pub fn set_mined_cargo(&mut self, cargo: i32) -> Result<(), CombatError> {
        if cargo < 0 {
            return Err(CombatError::InvalidStartingValue);
        }
        self.mined_cargo = cargo;
        self.tsv = self.calculate_tsv();
        Ok(())
    }
}

/// Disambiguate actual source and credit owner before allocating rewards.
/// These kinds correspond to existing RL diagnostics; they do not change
/// the historical command-owner snapshot attached to each shot.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DamageSource {
    Gun,
    Bomb,
    Charge,
    Explosion,
    DeathExplosion,
    Unattributed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HitEvent {
    pub projectile_id: i64,
    pub root_shot_id: i64,
    pub attacker_id: i64,
    pub target_id: i64,
    pub attacker_side: i32,
    pub target_side: i32,
    pub command_outcome_id: i64,
    pub source: DamageSource,
    pub delta: DamageDelta,
    /// Positive on attacks against enemies, negative on same-side damage.
    /// Self-inflicted damage may still have historical command credit,
    /// but RL reward attribution is handled separately from this value.
    pub command_tsv_delta: i32,
    pub self_inflicted: bool,
}

impl HitEvent {
    /// Ship.LogHitStats credits TSV in opposite directions for damage
    /// to enemies and friendlies, irrespective of RL shaping.
    pub fn new(
        projectile_id: i64,
        root_shot_id: i64,
        attacker_id: i64,
        target_id: i64,
        attacker_side: i32,
        target_side: i32,
        command_outcome_id: i64,
        source: DamageSource,
        delta: DamageDelta,
        self_inflicted: bool,
    ) -> Self {
        Self {
            projectile_id, root_shot_id, attacker_id, target_id,
            attacker_side, target_side, command_outcome_id, source,
            delta,
            command_tsv_delta: if attacker_side == target_side {
                -delta.tsv_loss
            } else {
                delta.tsv_loss
            },
            self_inflicted,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn partial_damage_and_lethal_damage_recalculate_full_ship_tsv() {
        let mut ship = ShipVitals::new(100, 100, 1000, 30).unwrap();
        assert_eq!(ship.tsv(), 1030);
        let first = ship.apply_damage(20, false).unwrap().unwrap();
        assert_eq!(first.applied_damage, 20);
        assert_eq!(first.tsv_loss, 100);
        assert_eq!(ship.tsv(), 930);
        let final_hit = ship.apply_damage(1000, false).unwrap().unwrap();
        assert_eq!(final_hit.applied_damage, 80);
        assert_eq!(final_hit.tsv_loss, 930);
        assert!(final_hit.killed);
        assert!(ship.apply_damage(1, false).unwrap().is_none());
    }

    #[test]
    fn harmless_projectile_preserves_health_and_value() {
        let mut ship = ShipVitals::new(100, 100, 1000, 0).unwrap();
        let delta = ship.apply_damage(500, true).unwrap().unwrap();
        assert_eq!(delta.applied_damage, 0);
        assert_eq!(delta.tsv_loss, 0);
        assert!(!delta.killed);
        assert_eq!(ship.health(), 100);
    }

    #[test]
    fn friendly_fire_penalizes_snapshot_command_credit() {
        let delta = DamageDelta {
            health_before: 100, health_after: 90, tsv_before: 1000,
            tsv_after: 950, applied_damage: 10, tsv_loss: 50, killed: false,
        };
        let hit = HitEvent::new(1, 1, 2, 3, 1, 1, 77, DamageSource::Gun, delta, false);
        assert_eq!(hit.command_tsv_delta, -50);
        assert_eq!(hit.command_outcome_id, 77);
    }
}
