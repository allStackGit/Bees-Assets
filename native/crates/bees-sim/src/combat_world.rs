//! World-local impact resolution atop the single native physics core.
//!
//! This is the shared game implementation, not a separate training simulator.
//! The core resolves contacts and health only where Unity rules are ported.
//! Command/squad storage, episode rewards, terminal-state decisions, and the
//! remaining weapon specials must be wired into this same world before the
//! world becomes usable for training or user-visible gameplay.
//!
//! Unity's OnTriggerEnter2D enqueues contact; Projectile.FixedUpdate consumes
//! one ship contact per ordinary projectile per tick, while RocketExplosion
//! drains all. We keep those queues at the world boundary. Within a tick we
//! use stable entity IDs for ordering; Unity callback ordering is not
//! guaranteed to match until checked with a live reference build.

use std::collections::{HashMap, HashSet};

use crate::collision::Aabb;
use crate::combat::{CombatError, DamageSource, HitEvent, ShipVitals};
use crate::damage::can_basic_projectile_damage;
use crate::physics::{
    CollisionMatrix, KinematicPhysics, PhysicsError, ProjectileBody, ShipBody, TriggerEnter,
};
use crate::policy::Decision;
use crate::projectile_rules::{ExplosionContacts, PiercingShot, PiercingType};
use crate::simulation::{GameRules, World};
use crate::telemetry::StepCounts;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatWorldError {
    Physics(PhysicsError),
    Combat(CombatError),
    DuplicateEntity,
    InvalidSide,
    InvalidProjectilePower,
    InvalidProjectileType(u8),
    /// Never fabricate simplified behavior for unported weapons.
    UnportedProjectileType(u8),
    UnsupportedPolicyActions,
}

impl From<PhysicsError> for CombatWorldError {
    fn from(value: PhysicsError) -> Self { Self::Physics(value) }
}
impl From<CombatError> for CombatWorldError {
    fn from(value: CombatError) -> Self { Self::Combat(value) }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CombatShip {
    pub entity_id: i64,
    pub side: i32,
    pub ship_type: u8,
    pub vitals: ShipVitals,
}

/// Per-projectile immutable owner information follows the shot after its
/// shooter's death, even if a pooled ship wrapper is reused for another unit.
/// Attribution IDs are carried from Projectile.Setup/Inheritance.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ShotOwner {
    pub shooter_id: i64,
    pub shooter_side: i32,
    pub shooter_is_fire_barge: bool,
    pub self_detonation: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CombatEvent {
    Hit(HitEvent),
    ShipDied {
        victim_id: i64,
        killer_id: i64,
        command_outcome_id: i64,
    },
    ProjectileEnded {
        projectile_id: i64,
    },
}

enum ImpactBehavior {
    Direct,
    Penetrating(PiercingShot),
    Explosion(ExplosionContacts),
}

impl ImpactBehavior {
    fn for_type(projectile_type: u8, power: i32) -> Result<Self, CombatWorldError> {
        match projectile_type {
            1 | 2 | 3 | 6 | 7 | 10 | 11 => Ok(Self::Direct),
            4 => Ok(Self::Penetrating(PiercingShot::new(PiercingType::PowerShot, power))),
            8 => Ok(Self::Penetrating(PiercingShot::new(PiercingType::LaserBeam, power))),
            13..=15 => Ok(Self::Explosion(ExplosionContacts::new())),
            5 | 9 | 12 => Err(CombatWorldError::UnportedProjectileType(projectile_type)),
            _ => Err(CombatWorldError::InvalidProjectileType(projectile_type)),
        }
    }

    fn drains_all_contacts(&self) -> bool {
        matches!(self, Self::Explosion(_))
    }
}

pub struct CombatProjectile {
    pub entity_id: i64,
    pub projectile_type: u8,
    pub owner: ShotOwner,
    pub power: i32,
    /// Exact shooter's shot-root and Hive Mind command identity live in the
    /// paired ProjectileBody.motion.attribution.
    pub ignored_ships: HashSet<i64>,
    behavior: ImpactBehavior,
}

impl CombatProjectile {
    pub fn new(
        entity_id: i64,
        projectile_type: u8,
        owner: ShotOwner,
        power: i32,
    ) -> Result<Self, CombatWorldError> {
        if power < 0 { return Err(CombatWorldError::InvalidProjectilePower); }
        let behavior = ImpactBehavior::for_type(projectile_type, power)?;
        Ok(Self {
            entity_id, projectile_type, owner, power,
            ignored_ships: HashSet::new(), behavior,
        })
    }

    pub fn set_explosion_harmless(&mut self, harmless: bool) {
        if let ImpactBehavior::Explosion(explosion) = &mut self.behavior {
            explosion.harmless = harmless;
        }
    }

    fn is_explosion(&self) -> bool {
        self.behavior.drains_all_contacts()
    }
}

/// State and scratch are owned per arena. No process globals, no cross-world
/// static dictionaries. Dense arrays use identical indices to physics bodies;
/// ID maps are only used when routing collision results.
pub struct CombatWorld {
    physics: KinematicPhysics,
    ships: Vec<CombatShip>,
    projectiles: Vec<CombatProjectile>,
    ship_by_id: HashMap<i64, usize>,
    projectile_by_id: HashMap<i64, usize>,
    pending_contacts: Vec<TriggerEnter>,
    processing_contacts: Vec<TriggerEnter>,
    processed_this_tick: HashSet<i64>,
    events: Vec<CombatEvent>,
    pub shots_harmless: bool,
    /// Dedicated RL episodes cannot apply further hits after game over.
    pub frozen_after_elimination: bool,
}

impl CombatWorld {
    pub fn new(area: Aabb, cell_size: f32) -> Result<Self, CombatWorldError> {
        Ok(Self {
            physics: KinematicPhysics::new(area, cell_size)?,
            ships: Vec::new(),
            projectiles: Vec::new(),
            ship_by_id: HashMap::new(),
            projectile_by_id: HashMap::new(),
            pending_contacts: Vec::new(),
            processing_contacts: Vec::new(),
            processed_this_tick: HashSet::new(),
            events: Vec::new(),
            shots_harmless: false,
            frozen_after_elimination: false,
        })
    }

    pub fn physics(&self) -> &KinematicPhysics { &self.physics }
    pub fn events(&self) -> &[CombatEvent] { &self.events }
    pub fn ships(&self) -> &[CombatShip] { &self.ships }
    pub fn projectiles(&self) -> &[CombatProjectile] { &self.projectiles }

    pub fn add_ship(
        &mut self,
        ship: CombatShip,
        body: ShipBody,
    ) -> Result<(), CombatWorldError> {
        if ship.side <= 0 { return Err(CombatWorldError::InvalidSide); }
        if body.id != ship.entity_id ||
            self.ship_by_id.contains_key(&ship.entity_id) ||
            self.projectile_by_id.contains_key(&ship.entity_id)
        {
            return Err(CombatWorldError::DuplicateEntity);
        }
        let index = self.physics.add_ship(body)?;
        debug_assert_eq!(index, self.ships.len());
        self.ship_by_id.insert(ship.entity_id, index);
        self.ships.push(ship);
        Ok(())
    }

    pub fn add_projectile(
        &mut self,
        projectile: CombatProjectile,
        body: ProjectileBody,
    ) -> Result<(), CombatWorldError> {
        if body.id != projectile.entity_id ||
            self.ship_by_id.contains_key(&projectile.entity_id) ||
            self.projectile_by_id.contains_key(&projectile.entity_id)
        {
            return Err(CombatWorldError::DuplicateEntity);
        }
        let index = self.physics.add_projectile(body)?;
        debug_assert_eq!(index, self.projectiles.len());
        self.projectile_by_id.insert(projectile.entity_id, index);
        self.projectiles.push(projectile);
        Ok(())
    }

    fn resolve_contact(&mut self, contact: TriggerEnter) -> Result<(), CombatWorldError> {
        let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
            return Ok(());
        };
        let Some(&ship_index) = self.ship_by_id.get(&contact.ship_id) else {
            return Ok(());
        };
        if !self.physics.projectiles()[shot_index].active ||
            !self.physics.ships()[ship_index].active {
            return Ok(());
        }
        let ship = &mut self.ships[ship_index];
        let shot = &mut self.projectiles[shot_index];
         let is_ignored = shot.ignored_ships.contains(&ship.entity_id);
        let shot_type = shot.projectile_type;
        let mut ends_projectile = false;
        let mut accepted_power = None;

        match &mut shot.behavior {
            ImpactBehavior::Explosion(explosion) => {
                if explosion.accept_ship(
                    ship.entity_id, shot.owner.shooter_side,
                    shot.owner.shooter_is_fire_barge, ship.side,
                    shot_type == 15, ship.vitals.is_dead()
                ) {
                    accepted_power = Some(shot.power);
                }
            }
            ImpactBehavior::Direct => {
                if can_basic_projectile_damage(
                    shot.owner.shooter_side,
                    shot.owner.shooter_is_fire_barge,
                    ship.side, is_ignored,
                ) {
                    accepted_power = Some(shot.power);
                    ends_projectile = true;
                }
            }
            ImpactBehavior::Penetrating(piercing) => {
                if can_basic_projectile_damage(
                    shot.owner.shooter_side,
                    shot.owner.shooter_is_fire_barge,
                    ship.side, is_ignored,
                ) {
                    if let Some(outcome) = piercing.contact_ship(
                        ship.entity_id, ship.vitals.health()
                    ) {
                        accepted_power = Some(outcome.damage_requested);
                        ends_projectile = outcome.ends_projectile;
                        shot.power = outcome.remaining_power;
                    }
                }
            }
        }

        // Unity calls ContactTarget() before Ship.LogAttackingDamage().
        // Ordinary shots and spent piercing shots therefore return to their
        // pool before the health, TSV, and kill callbacks run.
        if ends_projectile {
            self.physics.projectiles_mut()[shot_index].active = false;
            self.events.push(CombatEvent::ProjectileEnded {
                projectile_id: contact.projectile_id,
            });
        }

        if let Some(power) = accepted_power {
            let attribution = self.physics.projectiles()[shot_index].motion.attribution;
            if let Some(delta) = ship.vitals.apply_damage(power, self.shots_harmless)? {
                let source = match shot_type {
                    14 if shot.owner.self_detonation => DamageSource::Explosion,
                    14 => DamageSource::DeathExplosion,
                    _ => DamageSource::Gun,
                };
                let event = HitEvent::new(
                    shot.entity_id, attribution.root_shot_id,
                    shot.owner.shooter_id, ship.entity_id,
                    shot.owner.shooter_side, ship.side,
                    attribution.command_outcome_id,
                    source, delta, false,
                );
                self.events.push(CombatEvent::Hit(event));
                if delta.killed {
                    self.physics.ships_mut()[ship_index].active = false;
                    self.events.push(CombatEvent::ShipDied {
                        victim_id: ship.entity_id,
                        killer_id: shot.owner.shooter_id,
                        command_outcome_id: attribution.command_outcome_id,
                    });
                }
            }
        }

        Ok(())
    }

    /// This is a real collision -> health -> event path for direct, piercing
    /// and explosion projectiles. It is NOT a complete game/episode step.
    /// Contacts generated during physics are queued for the following step.
    pub fn step_fixed(
        &mut self,
        dt: f32,
        collision_matrix: &CollisionMatrix,
    ) -> Result<StepCounts, CombatWorldError> {
        self.events.clear();
        if self.frozen_after_elimination {
            return Ok(StepCounts {
                active_ships: self.ships.iter().filter(|ship| !ship.vitals.is_dead()).count() as u32,
                active_projectiles: self.physics.projectiles().iter().filter(|p| p.active).count() as u32,
                ..StepCounts::default()
            });
        }
        // Swap buffers instead of allocating a queue for every frame.
        std::mem::swap(&mut self.processing_contacts, &mut self.pending_contacts);
        self.pending_contacts.clear();
        self.processed_this_tick.clear();

        for index in 0..self.processing_contacts.len() {
            let contact = self.processing_contacts[index];
            let Some(&shot_index) = self.projectile_by_id.get(&contact.projectile_id) else {
                continue;
            };
            if !self.projectiles[shot_index].is_explosion() &&
                !self.processed_this_tick.insert(contact.projectile_id)
            {
                // Unity's ordinary Projectile.FixedUpdate consumes one ship
                // queue item; defer any remaining colliders.
                self.pending_contacts.push(contact);
                continue;
            }
            self.resolve_contact(contact)?;
        }
        self.processing_contacts.clear();

        // Base Projectile.FixedUpdate checks strict distance > Range after
        // contact processing, not on a newly fired shot or as a swept ray.
        // Rocket explosions override FixedUpdate and never range-expire here.
        for index in 0..self.projectiles.len() {
            if self.projectiles[index].is_explosion() {
                continue;
            }
            let projectile = &mut self.physics.projectiles_mut()[index];
            if projectile.active && projectile.motion.has_exceeded_range() {
                projectile.active = false;
                self.events.push(CombatEvent::ProjectileEnded {
                    projectile_id: projectile.id,
                });
            }
        }

        for ship in self.physics.ships_mut() {
            if ship.active {
                ship.motion.fixed_step_rl(dt);
            }
        }
        let new_contacts = self.physics.step(dt, collision_matrix)?;
        self.pending_contacts.extend_from_slice(new_contacts);

        Ok(StepCounts {
            active_ships: self.physics.ships().iter().filter(|ship| ship.active).count() as u32,
            active_projectiles: self.physics.projectiles().iter().filter(|shot| shot.active).count() as u32,
            events: self.events.len() as u64,
            ..StepCounts::default()
        })
    }
}

/// This is still a deliberately incomplete gameplay adapter. It refuses
/// neural actions until observations, rewards and full actuation are ported
/// into this same game world.
pub struct CombatWorldRules {
    pub collision_matrix: CollisionMatrix,
}

impl GameRules<CombatWorld> for CombatWorldRules {
    type Error = CombatWorldError;

    fn step(
        &self,
        world: &mut World<CombatWorld>,
        decisions: &[Decision],
        dt_seconds: f32,
    ) -> Result<StepCounts, Self::Error> {
        if !decisions.is_empty() {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        world.state.step_fixed(dt_seconds, &self.collision_matrix)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::Vec2;
    use crate::movement::ShipMotion;
    use crate::projectile::ProjectileMotion;
    use crate::ship_assets::ShipAssetCatalog;

    #[test]
    fn live_collision_debits_tsv_and_keeps_original_command_owner() {
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0,
        ).unwrap();
        let gunship = ships.by_type(13).unwrap();
        let body = gunship.spawn_physics_body(
            5, ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.add_ship(CombatShip {
            entity_id: 5, side: 2, ship_type: 13,
            vitals: ShipVitals::new(100, 100, 1000, 0).unwrap(),
        }, body).unwrap();

        let mut rows = [0u32; 32];
        rows[10] |= 1u32 << 12;
        let matrix = CollisionMatrix::from_rows(rows);
        let motion = ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 40.0, 9, 77);
        world.add_projectile(
            CombatProjectile::new(9, 1, ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            }, 20).unwrap(),
            ProjectileBody::new(9, motion, 10, 0.5).unwrap(),
        ).unwrap();

        // Physics dispatch first, then Unity-style queued impact resolution.
        assert!(world.step_fixed(0.02, &matrix).unwrap().events == 0);
        world.step_fixed(0.02, &matrix).unwrap();
        let events = world.events();
        assert_eq!(events.len(), 2);
        assert!(matches!(events[0], CombatEvent::ProjectileEnded { projectile_id: 9 }));
        let CombatEvent::Hit(hit) = &events[1] else { panic!("expected hit") };
        assert_eq!(hit.delta.applied_damage, 20);
        assert_eq!(hit.delta.tsv_loss, 100);
        assert_eq!(hit.command_outcome_id, 77);
        assert_eq!(hit.root_shot_id, 9);
        assert_eq!(world.ships()[0].vitals.health(), 80);
    }
}
