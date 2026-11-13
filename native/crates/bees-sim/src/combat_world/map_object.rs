//! Authoritative MapObject and Fire Tank gameplay in the shared combat world.
//!
//! Unity MapObject.OnTriggerEnter2D subtracts projectile power, kills at
//! health <= 0, then calls LastHitProjectile.KillSequence. We reuse the
//! already-ported rocket and splitter continuation paths, physical collider
//! slots, and GameState-style learned-visibility removal. The Fire Tank
//! continues the original incoming projectile's ownership and attribution
//! through a separate authored type-15 explosion. Its linked obstacle is
//! removed from gameplay; debris particles stay cosmetic in the renderer.

use super::*;
use crate::map_object::{FireTankConfig, MapObjectKind};

const FIRE_TANK_EXPLOSION_TYPE: u8 = 15;

impl CombatWorld {
    /// Register a real MapObject collider in the same KinematicPhysics as
    /// ordinary projectiles and static obstacles. The Level supplies exact
    /// prefab geometry, layer and initial object health. Map objects are NOT
    /// inserted in static navigation obstacle views or their discovery cache.
    pub fn add_map_object(
        &mut self,
        object: CombatMapObject,
        body: ObstacleBody,
    ) -> Result<(), CombatWorldError> {
        if (object.kind == MapObjectKind::Generic && object.fire_tank.is_some()) ||
            (object.kind == MapObjectKind::FireTank && object.fire_tank.is_none()) {
            return Err(CombatWorldError::InvalidObstacle);
        }
        if object.entity_id <= 0 || object.max_health <= 0 ||
            object.health != object.max_health ||
            object.entity_id != body.id || !body.active ||
            self.map_objects.contains_key(&object.entity_id) {
            return Err(CombatWorldError::InvalidObstacle);
        }
        // Validate at the world boundary as well as in the constructor:
        // canonical object records expose their fields for snapshots.
        if let Some(config) = object.fire_tank {
            if config.explosion_power < 0 {
                return Err(CombatWorldError::MapObject(
                    MapObjectError::InvalidFireTankPower,
                ));
            }
            if config.target_obstacle_id.is_some_and(|id|
                id <= 0 || id == object.entity_id) {
                return Err(CombatWorldError::MapObject(
                    MapObjectError::InvalidFireTankTarget,
                ));
            }
        }
        let entity_id = object.entity_id;
        self.add_obstacle(
            CombatObstacle {
                entity_id,
                kind: ObstacleKind::MapObject,
                vitals: None,
                size_class: 0,
            },
            body,
        )?;
        self.map_objects.insert(entity_id, object);
        Ok(())
    }

    /// Idempotently remove one MapObject from all authoritative game and
    /// faction-vision registries. The dense obstacle slot is retained for
    /// reuse; the monotonic runtime entity ID is never recycled.
    pub fn retire_map_object(&mut self, entity_id: i64) -> bool {
        let Some(&index) = self.obstacle_by_id.get(&entity_id) else {
            return false;
        };
        if self.obstacles[index].kind != ObstacleKind::MapObject ||
            !self.physics.obstacles()[index].active {
            return false;
        }
        self.physics.obstacles_mut()[index].active = false;
        self.obstacle_by_id.remove(&entity_id);
        self.map_objects.remove(&entity_id);
        self.range_visible_map_objects.remove(&entity_id);
        self.discovery.retire(DiscoveryKind::MapObject, entity_id);
        self.free_obstacle_slots.push(index);
        true
    }

    /// The source CanisterBomb.TargetObstacle.BreakApart removes the linked
    /// static obstacle from gameplay and marks navigation dirty. The particle
    /// debris is emitted by the rendering client, not by the headless world.
    /// Missing IDs are already-destroyed Unity objects and are ignored.
    fn break_fire_tank_obstacle(&mut self, tank_id: i64, linked_id: i64, origin: Vec2)
        -> Result<(), CombatWorldError>
    {
        let Some(&index) = self.obstacle_by_id.get(&linked_id) else {
            return Ok(());
        };
        if !self.physics.obstacles()[index].active {
            return Ok(());
        }
        if self.obstacles[index].kind != ObstacleKind::StaticObstacle {
            return Err(CombatWorldError::InvalidObstacle);
        }
        let body = &mut self.physics.obstacles_mut()[index];
        let bounds = body.primary_bounds().unwrap_or(Aabb {
            min: body.pose.position, max: body.pose.position,
        });
        body.active = false;
        // This wall was part of the authoritative pathfinder's static
        // clearance. Removing it must invalidate both synchronous cached
        // grids and interactive workers' immutable snapshots; the original
        // add-only revision would leave paths blocked after a Fire Tank
        // destroyed its wall.
        self.static_navigation_revision = self.static_navigation_revision.wrapping_add(1);
        self.obstacle_by_id.remove(&linked_id);
        // A destroyed animated wall cannot keep all subsequent map
        // searches in per-frame static rebuild mode indefinitely.
        if self.moving_static_navigation_obstacles {
            let still_moving = self.static_navigation_obstacles().any(|(_, body)|
                body.linear_velocity.length_squared() > 0.0 ||
                body.angular_velocity_degrees != 0.0,
            );
            self.moving_static_navigation_obstacles = still_moving;
        }
        self.discovery.retire(DiscoveryKind::Obstacle, linked_id);
        self.free_obstacle_slots.push(index);
        self.events.push(CombatEvent::FireTankObstacleBrokenApart {
            object_id: tank_id,
            obstacle_id: linked_id,
            origin,
            bounds,
        });
        Ok(())
    }

    /// Uses CanisterBomb.Kill's source shot attribution. Unlike a Rocket
    /// explosion, this is an EXTRA blast with its own projectile entity and
    /// no transferred damage reservation; original shot KillSequence runs
    /// afterward. Projectile type 15 retains source friendly-fire rules.
    fn spawn_fire_tank_explosion(
        &mut self,
        tank_id: i64,
        tank_position: Vec2,
        shot_index: usize,
        config: FireTankConfig,
    ) -> Result<(), CombatWorldError> {
        let source = &self.projectiles[shot_index];
        let source_id = source.entity_id;
        let owner = source.owner;
        let attribution = self.physics.projectiles()[shot_index].motion.attribution;
        let explosion_id = self.next_entity_id;
        let asset = self.projectile_assets.by_type(FIRE_TANK_EXPLOSION_TYPE)
            .ok_or(CombatWorldError::InvalidProjectileType(
                FIRE_TANK_EXPLOSION_TYPE,
            ))?.clone();
        let motion = ProjectileMotion::new(
            tank_position, 0.0, 0.0, 0.0,
            attribution.root_shot_id, attribution.command_outcome_id,
        );
        self.spawn_projectile(
            &asset, explosion_id, owner, config.explosion_power, motion, 0.0,
        )?;
        self.events.push(CombatEvent::FireTankExploded {
            object_id: tank_id,
            triggering_projectile_id: source_id,
            explosion_projectile_id: explosion_id,
        });
        Ok(())
    }

    /// An actual physics contact with a registered MapObject. We deliberately
    /// do not dispatch this through StaticObstacleImpact: unlike an ordinary
    /// wall, MapObject is destructible and calls projectile KillSequence.
    pub(super) fn resolve_map_object_contact(
        &mut self,
        contact: ObstacleTriggerEnter,
        shot_index: usize,
        obstacle_index: usize,
    ) -> Result<(), CombatWorldError> {
        let object_id = self.obstacles[obstacle_index].entity_id;
        let power = self.projectiles[shot_index].power;
        let Some(object) = self.map_objects.get(&object_id) else {
            return Err(CombatWorldError::InvalidObstacle);
        };
        if !object.alive() || power < 0 { return Ok(()); }

        let fatal = power >= object.health;
        // Source CanisterBomb.Kill spawns its explosion before returning
        // the incoming shot to the projectile pool. Preflight a linked
        // obstacle's kind before changing health or spawning anything.
        let fire_tank = if fatal { object.fire_tank } else { None };
        if let Some(config) = fire_tank {
            if let Some(linked_id) = config.target_obstacle_id {
                if let Some(&index) = self.obstacle_by_id.get(&linked_id) {
                    if self.physics.obstacles()[index].active &&
                        self.obstacles[index].kind != ObstacleKind::StaticObstacle {
                        return Err(CombatWorldError::InvalidObstacle);
                    }
                }
            }
            // Complete source validation before mutating MapObject health.
            // Type 15's collider and animation must exist in the authored
            // projectile catalog; fail closed if it does not.
            if self.projectile_assets.by_type(FIRE_TANK_EXPLOSION_TYPE).is_none() {
                return Err(CombatWorldError::InvalidProjectileType(
                    FIRE_TANK_EXPLOSION_TYPE,
                ));
            }
            if self.next_entity_id == i64::MAX {
                return Err(CombatWorldError::EntityIdExhausted);
            }
            if config.explosion_power < 0 {
                return Err(CombatWorldError::MapObject(
                    MapObjectError::InvalidFireTankPower,
                ));
            }
        }

        // Unity subtracts Health and invokes OnHealthChanged before
        // CanisterBomb.Kill creates the explosion and breaks its obstacle.
        let damage = self.map_objects.get_mut(&object_id)
            .expect("validated object").apply_projectile_damage(power)
            .expect("validated live object and power");
        self.events.push(CombatEvent::MapObjectDamaged {
            projectile_id: contact.projectile_id,
            object_id,
            damage,
        });
        if damage.destroyed {
            if let Some(config) = fire_tank {
                let origin = self.physics.obstacles()[obstacle_index].pose.position;
                self.spawn_fire_tank_explosion(object_id, origin, shot_index, config)?;
                if let Some(linked_id) = config.target_obstacle_id {
                    self.break_fire_tank_obstacle(object_id, linked_id, origin)?;
                }
            }
            self.retire_map_object(object_id);
            self.events.push(CombatEvent::MapObjectDestroyed {
                object_id,
                projectile_id: contact.projectile_id,
            });
        }

        // Unity MapObject.OnTriggerEnter2D always calls the projectile's
        // virtual KillSequence, not its ContactObstacle implementation.
        // Rocket and SplitterShot continue using their real gameplay
        // continuation, preserving root shot and command attribution.
        if matches!(
            &self.projectiles[shot_index].behavior,
            ImpactBehavior::Rocket { .. },
        ) {
            self.spawn_rocket_explosion(shot_index)?;
        } else if matches!(
            &self.projectiles[shot_index].behavior,
            ImpactBehavior::Splitter { .. },
        ) {
            self.spawn_split_children(shot_index, None)?;
        }
        self.end_projectile(shot_index);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::collision::Pose;
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::projectile::ProjectileMotion;
    use crate::combat::ShipVitals;
    use crate::movement::ShipMotion;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::combat_perception_bridge::collect_learned_map_objects;
    use crate::vision::HiveMindObserver;

    #[test]
    fn fire_tank_death_spawns_attributed_friendly_fire_explosion_and_breaks_linked_wall() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-100.0, -100.0),
                max: Vec2::new(100.0, 100.0),
            },
            8.0,
            Arc::clone(&assets),
        ).unwrap();

        let wall_id = 1;
        world.add_obstacle(
            CombatObstacle {
                entity_id: wall_id,
                kind: ObstacleKind::StaticObstacle,
                vitals: None,
                size_class: 0,
            },
            ObstacleBody::new(
                wall_id,
                Pose {
                    position: Vec2::new(20.0, 0.0),
                    rotation_degrees: 0.0,
                },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(8.0, 5.0),
                },
                Vec2::ZERO,
                0.0,
            ).unwrap(),
        ).unwrap();
        let tank_id = 2;
        let tank_position = Vec2::new(5.0, 0.0);
        world.add_map_object(
            CombatMapObject::new_fire_tank(
                tank_id, 100, 40, Some(wall_id),
            ).unwrap(),
            ObstacleBody::new(
                tank_id,
                Pose {
                    position: tank_position,
                    rotation_degrees: 0.0,
                },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(2.0, 2.0),
                },
                Vec2::ZERO,
                0.0,
            ).unwrap(),
        ).unwrap();
        let shooter_id = 3;
        world.spawn_ship(
            hulls.by_type(21).unwrap(), shooter_id, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 0.0, 3.0),
        ).unwrap();
        world.discovery_mut().record(
            1, DiscoveryKind::MapObject, tank_id,
        ).unwrap();
        world.discovery_mut().record(
            1, DiscoveryKind::Obstacle, wall_id,
        ).unwrap();

        let shot_id = 4;
        world.spawn_projectile(
            assets.by_type(1).unwrap(), shot_id,
            ShotOwner {
                shooter_id,
                shooter_side: 1,
                shooter_is_fire_barge: false,
                self_detonation: false,
            },
            120,
            ProjectileMotion::new(
                Vec2::ZERO, 10.0, 0.0, 100.0, 900, 501,
            ),
            0.0,
        ).unwrap();
        world.resolve_obstacle_contact(ObstacleTriggerEnter {
            projectile_id: shot_id, obstacle_id: tank_id,
        }).unwrap();

        assert!(world.map_object(tank_id).is_none());
        assert!(!world.obstacle_by_id.contains_key(&wall_id));
        assert!(!world.discovery().has(
            1, DiscoveryKind::MapObject, tank_id,
        ));
        assert!(!world.discovery().has(
            1, DiscoveryKind::Obstacle, wall_id,
        ));
        let blast_id = 5;
        let blast_index = *world.projectile_by_id.get(&blast_id).unwrap();
        let blast = &world.projectiles()[blast_index];
        assert_eq!(blast.projectile_type, FIRE_TANK_EXPLOSION_TYPE);
        assert_eq!(blast.power, 40);
        assert_eq!(blast.owner.shooter_id, shooter_id);
        assert_eq!(
            world.physics.projectiles()[blast_index].motion.attribution,
            crate::projectile::ShotAttribution {
                root_shot_id: 900,
                command_outcome_id: 501,
            },
        );
        assert_eq!(
            world.physics.projectiles()[blast_index].motion.position,
            tank_position,
        );
        assert!(world.physics.projectiles()[blast_index].active);
        assert!(!world.physics.projectiles()[
            *world.projectile_by_id.get(&shot_id).unwrap()
        ].active);
        assert!(world.events().iter().any(|event| matches!(
            event,
            CombatEvent::FireTankExploded {
                object_id: 2,
                triggering_projectile_id: 4,
                explosion_projectile_id: 5,
            }
        )));
        assert!(world.events().iter().any(|event| matches!(
            event,
            CombatEvent::FireTankObstacleBrokenApart {
                object_id: 2, obstacle_id: 1, ..
            }
        )));

        // Type 15 explicitly permits same-side damage, unlike ordinary
        // explosions; the original shooter's attribution survives.
        world.resolve_contact(TriggerEnter {
            projectile_id: blast_id,
            ship_id: shooter_id,
        }).unwrap();
        assert_eq!(world.ship(shooter_id).unwrap().vitals.health(), 60);
        assert!(world.events().iter().any(|event| matches!(
            event,
            CombatEvent::Hit(hit)
                if hit.root_shot_id == 900 &&
                   hit.command_outcome_id == 501
        )));
    }

    #[test]
    fn real_map_object_collider_is_seen_by_edge_and_projected_from_world() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let ships = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-100.0, -100.0),
                max: Vec2::new(100.0, 100.0),
            },
            8.0,
            assets,
        ).unwrap();
        world.spawn_ship(
            ships.by_type(21).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 0.0, 3.0),
        ).unwrap();
        world.add_map_object(
            CombatMapObject::new(2, MapObjectKind::Generic, 50).unwrap(),
            ObstacleBody::new(
                2,
                Pose {
                    position: Vec2::new(20.0, 0.0),
                    rotation_degrees: 0.0,
                },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(18.0, 2.0),
                },
                Vec2::ZERO,
                0.0,
            ).unwrap(),
        ).unwrap();
        let observer = [HiveMindObserver {
            ship_id: 1,
            effective_range: 5,
            registered: true,
            enabled: true,
        }];
        let sweep = world.refresh_hivemind_vision(&observer, false).unwrap();
        assert_eq!(sweep.first_map_object_discoveries, 1);
        assert!(world.discovery().has(1, DiscoveryKind::MapObject, 2));
        assert!(!world.discovery().has(1, DiscoveryKind::Obstacle, 2));
        let mut observed = Vec::new();
        assert_eq!(collect_learned_map_objects(
            &world, 1, &mut observed,
        ).unwrap(), 1);
        assert_eq!(observed[0].position, Vec2::new(20.0, 0.0));
        assert_eq!(observed[0].half_extents, Vec2::new(18.0, 2.0));
        assert_eq!(observed[0].health_fraction, 1.0);
        assert!(world.retire_map_object(2));
        assert!(!world.retire_map_object(2));
        assert_eq!(collect_learned_map_objects(
            &world, 1, &mut observed,
        ).unwrap(), 0);
        assert!(!world.discovery().has(1, DiscoveryKind::MapObject, 2));
    }

    #[test]
    fn ordinary_map_object_damage_and_discovery_use_the_shared_combat_world() {
        let assets = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-100.0, -100.0),
                max: Vec2::new(100.0, 100.0),
            },
            8.0,
            Arc::clone(&assets),
        ).unwrap();
        let id = 1;
        world.add_map_object(
            CombatMapObject::new(id, MapObjectKind::Generic, 100).unwrap(),
            ObstacleBody::new(
                id,
                Pose { position: Vec2::new(10.0, 0.0),
                    rotation_degrees: 0.0 },
                0,
                ShipShape::Box {
                    offset: Vec2::ZERO,
                    half_extents: Vec2::new(5.0, 5.0),
                },
                Vec2::ZERO,
                0.0,
            ).unwrap(),
        ).unwrap();
        assert!(world.discovery_mut().record(
            1, DiscoveryKind::MapObject, id,
        ).unwrap());

        let owner = ShotOwner {
            shooter_id: 55,
            shooter_side: 1,
            shooter_is_fire_barge: false,
            self_detonation: false,
        };
        for (shot, power) in [(2, 30), (3, 90)] {
            world.spawn_projectile(
                assets.by_type(1).unwrap(),
                shot,
                owner,
                power,
                ProjectileMotion::new(
                    Vec2::ZERO, 10.0, 0.0, 100.0, shot, 0,
                ),
                0.0,
            ).unwrap();
            world.resolve_obstacle_contact(
                ObstacleTriggerEnter { projectile_id: shot,
                    obstacle_id: id },
            ).unwrap();
            assert!(!world.physics.projectiles()[
                *world.projectile_by_id.get(&shot).unwrap()
            ].active);
            if shot == 2 {
                assert_eq!(world.map_object(id).unwrap().health, 70);
                assert!(world.discovery().has(
                    1, DiscoveryKind::MapObject, id,
                ));
            }
        }
        assert!(world.map_object(id).is_none());
        assert!(!world.discovery().has(
            1, DiscoveryKind::MapObject, id,
        ));
        assert_eq!(world.map_object_count(), 0);
        assert!(!world.obstacle_by_id.contains_key(&id));
        assert!(world.events().iter().any(|event| matches!(
            event, CombatEvent::MapObjectDestroyed {
                object_id: 1, projectile_id: 3,
            }
        )));
    }
}
