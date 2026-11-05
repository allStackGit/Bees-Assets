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
