//! Authoritative ordinary MapObject gameplay in the existing combat world.
//!
//! Unity MapObject.OnTriggerEnter2D subtracts projectile power, kills at
//! health <= 0, then calls LastHitProjectile.KillSequence. We reuse the
//! already-ported rocket and splitter continuation paths, physical collider
//! slots, and GameState-style learned-visibility removal. Fire Tank remains
//! deliberately unavailable: CanisterBomb.Kill requires its own attributed
//! FireTankExplosion and map-debris breakup mechanics.

use super::*;
use crate::perception_environment::MapObjectKind;

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
        if object.kind != MapObjectKind::Generic {
            return Err(CombatWorldError::MapObject(
                MapObjectError::UnsupportedFireTank,
            ));
        }
        if object.entity_id <= 0 || object.max_health <= 0 ||
            object.health != object.max_health ||
            object.entity_id != body.id ||
            self.map_objects.contains_key(&object.entity_id) {
            return Err(CombatWorldError::InvalidObstacle);
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
        self.discovery.retire(DiscoveryKind::MapObject, entity_id);
        self.free_obstacle_slots.push(index);
        true
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
        let Some(object) = self.map_objects.get_mut(&object_id) else {
            return Err(CombatWorldError::InvalidObstacle);
        };
        let Some(damage) = object.apply_projectile_damage(power) else {
            return Ok(());
        };
        self.events.push(CombatEvent::MapObjectDamaged {
            projectile_id: contact.projectile_id,
            object_id,
            damage,
        });
        if damage.destroyed {
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
