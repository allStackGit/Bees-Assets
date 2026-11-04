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
use std::sync::Arc;

use crate::collision::Aabb;
use crate::combat::{CombatError, DamageSource, HitEvent, ShipVitals};
use crate::damage::can_basic_projectile_damage;
use crate::damage_reservations::{DamageReservations, DamageStatus, ReservationError};
use crate::physics::{
    CollisionMatrix, KinematicPhysics, PhysicsError, ProjectileBody, ShipBody, TriggerEnter,
};
use crate::policy::Decision;
use crate::movement::ShipMotion;
use crate::projectile::ProjectileMotion;
use crate::ship_assets::ShipAsset;
use crate::projectile_assets::ProjectileAsset;
use crate::projectile_rules::{rocket_accelerated_velocity, ExplosionContacts, PiercingShot, PiercingType};
use crate::projectile_assets::ProjectileAssetCatalog;
use crate::timer::ScaledTimer;
use crate::simulation::{GameRules, World};
use crate::telemetry::StepCounts;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CombatWorldError {
    Physics(PhysicsError),
    Combat(CombatError),
    Reservation(ReservationError),
    UnknownProjectile,
    UnknownShip,
    EntityIdExhausted,
    MissingRocketStats,
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
impl From<ReservationError> for CombatWorldError {
    fn from(value: ReservationError) -> Self { Self::Reservation(value) }
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
    ProjectileContinued {
        parent_id: i64,
        continuation_id: i64,
    },
}

enum ImpactBehavior {
    Direct,
    Rocket { acceleration_timer: ScaledTimer, max_speed: f32 },
    Penetrating(PiercingShot),
    Explosion(ExplosionContacts),
}

impl ImpactBehavior {
    fn for_type(projectile_type: u8, power: i32, rocket_max_speed: Option<f32>) -> Result<Self, CombatWorldError> {
        match projectile_type {
            1 | 2 | 3 | 6 | 7 | 10 | 11 => Ok(Self::Direct),
            4 => Ok(Self::Penetrating(PiercingShot::new(PiercingType::PowerShot, power))),
            8 => Ok(Self::Penetrating(PiercingShot::new(PiercingType::LaserBeam, power))),
            13..=15 => Ok(Self::Explosion(ExplosionContacts::new())),
            5 => Ok(Self::Rocket {
                acceleration_timer: ScaledTimer::new(0.1, true, true),
                max_speed: rocket_max_speed.ok_or(CombatWorldError::MissingRocketStats)?,
            }),
            9 | 12 => Err(CombatWorldError::UnportedProjectileType(projectile_type)),
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
        rocket_max_speed: Option<f32>,
    ) -> Result<Self, CombatWorldError> {
        if power < 0 { return Err(CombatWorldError::InvalidProjectilePower); }
        let behavior = ImpactBehavior::for_type(projectile_type, power, rocket_max_speed)?;
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
    projectile_assets: Arc<ProjectileAssetCatalog>,
    next_entity_id: i64,
    ships: Vec<CombatShip>,
    projectiles: Vec<CombatProjectile>,
    ship_by_id: HashMap<i64, usize>,
    projectile_by_id: HashMap<i64, usize>,
    reservations: DamageReservations,
    pending_contacts: Vec<TriggerEnter>,
    processing_contacts: Vec<TriggerEnter>,
    processed_this_tick: HashSet<i64>,
    events: Vec<CombatEvent>,
    pub shots_harmless: bool,
    /// Dedicated RL episodes cannot apply further hits after game over.
    pub frozen_after_elimination: bool,
}

impl CombatWorld {
    pub fn new(
        area: Aabb,
        cell_size: f32,
        projectile_assets: Arc<ProjectileAssetCatalog>,
    ) -> Result<Self, CombatWorldError> {
        Ok(Self {
            physics: KinematicPhysics::new(area, cell_size)?,
            projectile_assets,
            next_entity_id: 1,
            ships: Vec::new(),
            projectiles: Vec::new(),
            ship_by_id: HashMap::new(),
            projectile_by_id: HashMap::new(),
            reservations: DamageReservations::new(),
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
    pub fn reservations(&self) -> &DamageReservations { &self.reservations }

    /// The canonical spawn path for game and training. Asset collider
    /// geometry is shared, not reconstructed per world or per spawn.
    pub fn spawn_ship(
        &mut self,
        asset: &ShipAsset,
        entity_id: i64,
        side: i32,
        vitals: ShipVitals,
        motion: ShipMotion,
    ) -> Result<(), CombatWorldError> {
        let body = asset.spawn_physics_body(entity_id, motion)?;
        self.add_ship(CombatShip {
            entity_id, side, ship_type: asset.ship_type, vitals,
        }, body)
    }

    /// Projectile kind and shape are supplied by the SAME authored prefab
    /// catalog that the rendered client uses for spawning.
    pub fn spawn_projectile(
        &mut self,
        asset: &ProjectileAsset,
        entity_id: i64,
        owner: ShotOwner,
        power: i32,
        motion: ProjectileMotion,
        rotation_degrees: f32,
    ) -> Result<(), CombatWorldError> {
        let metadata = CombatProjectile::new(
            entity_id, asset.projectile_type, owner, power, asset.rocket_max_speed,
        )?;
        let body = asset.spawn_physics_body(entity_id, motion, rotation_degrees)?;
        self.add_projectile(metadata, body)
    }

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
        let next_id = body.id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        let index = self.physics.add_ship(body)?;
        self.next_entity_id = self.next_entity_id.max(next_id);
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
        let next_id = body.id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        let index = self.physics.add_projectile(body)?;
        self.next_entity_id = self.next_entity_id.max(next_id);
        debug_assert_eq!(index, self.projectiles.len());
        self.projectile_by_id.insert(projectile.entity_id, index);
        self.projectiles.push(projectile);
        Ok(())
    }

    /// Bind the existing weapon's outgoing-damage reservation exactly once.
    /// Projectile.Setup only captures this handle; it never creates a second
    /// reservation. The final weapon port will call this when firing.
    pub fn reserve_projectile_damage(
        &mut self,
        projectile_id: i64,
        target_id: i64,
    ) -> Result<DamageStatus, CombatWorldError> {
        let shot_index = *self.projectile_by_id.get(&projectile_id)
            .ok_or(CombatWorldError::UnknownProjectile)?;
        let target_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.projectiles()[shot_index].active ||
            !self.physics.ships()[target_index].active {
            return Err(CombatWorldError::Reservation(ReservationError::TargetUnavailable));
        }
        let shot = &self.projectiles[shot_index];
        let health = self.ships[target_index].vitals.health();
        Ok(self.reservations.reserve(
            projectile_id, shot.owner.shooter_side, target_id, health, shot.power,
        )?)
    }

    /// The target-selection command can consult the same reserved-damage
    /// counter as GameState.GetShipDamageStatus without copying the ledger.
    pub fn target_damage_status(&mut self, side: i32, target_id: i64)
        -> Result<DamageStatus, CombatWorldError>
    {
        let ship_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        Ok(self.reservations.status(
            side, target_id, self.ships[ship_index].vitals.health(),
        ))
    }

    /// Return a shot to the dormant state once, release only its OWN
    /// reservation, and notify other gameplay systems in Unity's order.
    /// Rockets transfer their lease to the explosion before calling this.
    fn end_projectile(&mut self, index: usize) {
        let projectile = &mut self.physics.projectiles_mut()[index];
        if !projectile.active {
            return;
        }
        projectile.active = false;
        let id = projectile.id;
        self.reservations.release(id);
        self.events.push(CombatEvent::ProjectileEnded { projectile_id: id });
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

        // Scope mutable shot-state borrow before changing the world or
        // returning the projectile to its pool. This is also the source's
        // ContactTarget-before-LogAttackingDamage event ordering.
        let target = &self.ships[ship_index];
        let shot = &mut self.projectiles[shot_index];
        let owner = shot.owner;
        let shot_type = shot.projectile_type;
        let mut ends_projectile = false;
        let mut accepted_power = None;

        match &mut shot.behavior {
            ImpactBehavior::Explosion(explosion) => {
                if explosion.accept_ship(
                    target.entity_id, owner.shooter_side,
                    owner.shooter_is_fire_barge, target.side,
                    shot_type == 15, target.vitals.is_dead(),
                ) {
                    accepted_power = Some(shot.power);
                }
            }
            ImpactBehavior::Direct => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    accepted_power = Some(shot.power);
                    ends_projectile = true;
                }
            }
            ImpactBehavior::Penetrating(piercing) => {
                if can_basic_projectile_damage(
                    owner.shooter_side, owner.shooter_is_fire_barge,
                    target.side, shot.ignored_ships.contains(&target.entity_id),
                ) {
                    if let Some(outcome) = piercing.contact_ship(
                        target.entity_id, target.vitals.health(),
                    ) {
                        accepted_power = Some(outcome.damage_requested);
                        ends_projectile = outcome.ends_projectile;
                        shot.power = outcome.remaining_power;
                    }
                }
            }
        }

        if ends_projectile {
            self.end_projectile(shot_index);
        }
        if let Some(power) = accepted_power {
            let attribution = self.physics.projectiles()[shot_index].motion.attribution;
            let target = &mut self.ships[ship_index];
            if let Some(delta) = target.vitals.apply_damage(power, self.shots_harmless)? {
                self.reservations.target_health_changed(target.entity_id, target.vitals.health());
                let source = match shot_type {
                    14 if owner.self_detonation => DamageSource::Explosion,
                    14 => DamageSource::DeathExplosion,
                    _ => DamageSource::Gun,
                };
                self.events.push(CombatEvent::Hit(HitEvent::new(
                    contact.projectile_id, attribution.root_shot_id,
                    owner.shooter_id, target.entity_id,
                    owner.shooter_side, target.side,
                    attribution.command_outcome_id,
                    source, delta, false,
                )));
                if delta.killed {
                    self.physics.ships_mut()[ship_index].active = false;
                    self.events.push(CombatEvent::ShipDied {
                        victim_id: target.entity_id,
                        killer_id: owner.shooter_id,
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
            if self.physics.projectiles()[index].active &&
                self.physics.projectiles()[index].motion.has_exceeded_range() {
                self.end_projectile(index);
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
    use crate::projectile_assets::ProjectileAssetCatalog;

    #[test]
    fn live_collision_debits_tsv_and_keeps_original_command_owner() {
        let ships = ShipAssetCatalog::embedded().unwrap();
        let projectile_assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            4.0, projectile_assets.clone(),
        ).unwrap();
        world.spawn_ship(
            ships.by_type(13).unwrap(), 5, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();

        let mut rows = [0u32; 32];
        rows[10] |= 1u32 << 12;
        let matrix = CollisionMatrix::from_rows(rows);
        let motion = ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 40.0, 9, 77);
        world.spawn_projectile(
            projectile_assets.by_type(1).unwrap(),
            9,
            ShotOwner {
                shooter_id: 3, shooter_side: 1,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            20, motion, 0.0,
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
