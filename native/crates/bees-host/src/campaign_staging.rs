//! Source Titania II player-fleet staging before the native Level is built.
//! This is one-time authored mission setup, never a second movement engine.

use std::collections::{HashMap, HashSet};

use bees_gameplay::player_inputs::NativeLevelPlayerContext;
use bees_gameplay::campaign_dialogue::TITANIA_II_CENTER;
use bees_sim::geometry::Vec2;
use bees_sim::physics::ObstacleBody;
use bees_sim::obstacle::ObstacleKind;
use bees_sim::box_obstacle_assets::BoxObstacleCatalog;
use bees_sim::asteroid_assets::CollisionAsteroidCatalog;
use bees_sim::polygon_obstacle_assets::PolygonObstacleCatalog;
use bees_sim::ship_assets::HUMAN_TARGET_TYPE;
use bees_sim::squad_movement::authored_placement_offset;

use crate::policy_only_metadata::NativePolicyOnlyMetadata;
use crate::scene_builder::{NativeAuthoredEntity, NativeAuthoredLevel, NativeAuthoredShip};

struct Placement {
    index: usize,
    position: Vec2,
    size: Vec2,
    ship_type: u8,
    source_offset: Vec2,
}

fn geometry(slots: &[Placement]) -> (Vec2, f32, f32) {
    let mut left = f32::INFINITY;
    let mut right = f32::NEG_INFINITY;
    let mut bottom = f32::INFINITY;
    let mut top = f32::NEG_INFINITY;
    for slot in slots {
        left = left.min(slot.position.x - slot.size.x * 0.5);
        right = right.max(slot.position.x + slot.size.x * 0.5);
        bottom = bottom.min(slot.position.y - slot.size.y * 0.5);
        top = top.max(slot.position.y + slot.size.y * 0.5);
    }
    (Vec2::new((left + right) * 0.5, (bottom + top) * 0.5),
     right - left, top - bottom)
}

fn place_at_center(radius: f32, occupied: &[(Vec2, f32)],
                   obstacles: &[ObstacleBody], min: Vec2, max: Vec2) -> Option<Vec2> {
    // Titania2Beenoculars.FindTitania2HumanSquadPlacement: ring-first,
    // x-major/y-minor, preserving Unity's first clear candidate tie-break.
    for ring in 1i32..=12 {
        for x in -ring..=ring {
            for y in -ring..=ring {
                if x.abs().max(y.abs()) != ring { continue; }
                let candidate = TITANIA_II_CENTER + Vec2::new(x as f32 * 24.0, y as f32 * 24.0);
                if candidate.x - radius < min.x || candidate.x + radius > max.x ||
                    candidate.y - radius < min.y || candidate.y + radius > max.y {
                    continue;
                }
                let distance = (candidate - TITANIA_II_CENTER).length_squared().sqrt();
                if distance < 22.0 + radius { continue; }
                if occupied.iter().any(|&(center, prior_radius)|
                    (candidate - center).length_squared().sqrt() < radius + prior_radius
                ) || obstacles.iter().any(|body|
                    body.navigation_closest_point(candidate).is_some_and(|point|
                        (candidate - point).length_squared() < radius * radius)
                ) {
                    continue;
                }
                return Some(candidate);
            }
        }
    }
    // Never stage a physical fleet outside the map or inside an authored
    // collider when no candidate fits; the caller keeps its original scene.
    None
}

/// Resolve immutable authored obstacle bodies using the same source prefabs
/// and exact navigation hulls that the ordinary CombatWorld installs.
/// Called only during one-time campaign or reinforcement authoring.
pub(crate) fn authored_obstacle_bodies(
    entities: &[NativeAuthoredEntity],
) -> Result<Vec<ObstacleBody>, &'static str> {
    let boxes = BoxObstacleCatalog::embedded().map_err(|_| "Native level needs valid source box assets")?;
    let polygons = PolygonObstacleCatalog::embedded().map_err(|_| "Native level needs valid source polygon assets")?;
    let mut obstacles = Vec::new();
    let asteroids = CollisionAsteroidCatalog::embedded().map_err(|_| "Native level needs valid source asteroid assets")?;
    for entity in entities {
            let body = match entity {
                NativeAuthoredEntity::BoxObstacle {
                    entity_id, prefab_name, pose, live_corners,
                } => {
                    let asset = boxes
                        .by_name(prefab_name)
                        .ok_or("Native level obstacle lacks its source prefab")?;
                    if asset.kind != ObstacleKind::StaticObstacle { continue; }
                    let (_, body) = if let Some(corners) = live_corners {
                        asset.prepare_spawn_live_box(*entity_id,
                            pose.ok_or("Native level live box has no pose")?, *corners)
                    } else { asset.prepare_spawn(*entity_id, *pose) }
                        .map_err(|_| "Native level box obstacle has invalid geometry")?;
                    body
                }
                NativeAuthoredEntity::PolygonObstacle {
                    entity_id, pose, live_vertices,
                } => {
                    let catalog = &polygons;
                    let (_, body) = if let Some(vertices) = live_vertices {
                        catalog.prepare_spawn_live_polygon(*entity_id,
                            pose.ok_or("Native level live polygon has no pose")?, vertices.clone())
                    } else { catalog.prepare_spawn(*entity_id, *pose) }
                        .map_err(|_| "Native level polygon obstacle has invalid geometry")?;
                    body
                }
                NativeAuthoredEntity::MapObject { body, .. } |
                NativeAuthoredEntity::MiningAsteroid { body, .. } => body.clone(),
                NativeAuthoredEntity::FullCollisionAsteroid {
                    entity_id, profile, pose, velocity,
                    angular_velocity_degrees, effective_max_health,
                } => {
                    let (_, body) = asteroids
                        .prepare_spawn(profile.prefab_number, *entity_id,
                            *pose, *velocity, *angular_velocity_degrees,
                            Some(*effective_max_health))
                        .map_err(|_| "Native level asteroid has invalid source geometry")?;
                    body
                },
                NativeAuthoredEntity::Ship(_) => continue,
            };
            if body.active { obstacles.push(body); }
    }

    Ok(obstacles)
}

impl<A> NativeAuthoredLevel<NativePolicyOnlyMetadata, A> {
    /// Unity StageTitania2HumanFleetAtCenter runs BEFORE the briefing and
    /// before its four initial enemy arrivals. Reposition real player squads
    /// only when the source snapshot has not already staged them (the source
    /// StartingPositions entry is the authoritative signal).
    ///
    /// Search original source ring positions against the same physical
    /// static colliders used by the native Level and its pathfinder.
    pub(crate) fn stage_titania2_player_fleet(
        &mut self, player_side: i32,
    ) -> Result<(), &'static str> {
        if !(1..=2).contains(&player_side) {
            return Err("Titania II needs source player side 1 or 2");
        }
        let index = (player_side - 1) as usize;
        let start_positions = self.metadata.hivemind_starting_positions
            .ok_or("Titania II needs source Stage starting positions")?;
        if start_positions[index].unity_equal(TITANIA_II_CENTER) {
            // Already positioned in a captured live Unity campaign scene.
            return Ok(());
        }
        // Materialize only the immutable source obstacle hulls for the
        // one-time staging decision. This neither builds a second CombatWorld
        // nor creates a campaign-specific movement or collision engine.
        let has_obstacles = self.metadata.player_formation_options().has_obstacles;
        let obstacles = if has_obstacles {
            authored_obstacle_bodies(&self.entities)?
        } else { Vec::new() };

        let mut index_by_id = HashMap::new();
        for (index, entity) in self.entities.iter().enumerate() {
            if let NativeAuthoredEntity::Ship(ship) = entity {
                if index_by_id.insert(ship.entity_id, index).is_some() {
                    return Err("Titania II contains duplicate source ship identities");
                }
            }
        }
        // Already-deployed opposing hulls are genuine initial-World bodies.
        // Keep player staging clear of them as well as obstacles and other
        // newly staged player squads; deferred waves have been removed by
        // the normal scene constructor before this method is invoked.
        let mut occupied: Vec<(Vec2, f32)> = Vec::new();
        for entity in &self.entities {
            let NativeAuthoredEntity::Ship(ship) = entity else { continue };
            if ship.side == player_side || ship.vitals.is_dead() { continue; }
            let size = self.metadata.ship_catalog.sprite_size(ship.ship_type)
                .ok_or("Titania II opposing ship lacks source sprite dimensions")?;
            if !size.is_finite() || size.x <= 0.0 || size.y <= 0.0 ||
                !ship.motion.position.is_finite() {
                return Err("Titania II opposing ship has invalid physical geometry");
            }
            occupied.push((ship.motion.position, size.x.max(size.y) * 0.5 + 8.0));
        }
        // Resolve the complete fleet before modifying the physical scene.
        // A malformed later squad must not leave earlier squads relocated.
        let mut placements = Vec::new();
        let mut staged_ship_ids = HashSet::new();
        let mut slots = Vec::new();
        let bounds = self.broadphase_bounds;
        // Level.State.GetSquadsBySide preserves this authored squad order.
        for squad in &self.squads {
            if squad.side != player_side || squad.members.is_empty() { continue; }
            slots.clear();
            for &id in &squad.members {
                if !staged_ship_ids.insert(id) {
                    return Err("Titania II player ship belongs to multiple staged squads");
                }
                let &index = index_by_id.get(&id)
                    .ok_or("Titania II player squad references an absent ship")?;
                let NativeAuthoredEntity::Ship(NativeAuthoredShip {
                    ship_type, motion, ..
                }) = &self.entities[index] else {
                    return Err("Titania II player squad references a non-ship entity");
                };
                let size = self.metadata.ship_catalog.sprite_size(*ship_type)
                    .ok_or("Titania II player ship has no authored sprite dimensions")?;
                let offset = self.metadata.stored_formation_offset(id)
                    .ok_or("Titania II player ship has no source formation offset")?;
                if !offset.is_finite() {
                    return Err("Titania II player formation offset is nonfinite");
                }
                slots.push(Placement {
                    index, position: motion.position, size,
                    ship_type: *ship_type, source_offset: offset,
                });
            }
            // Unity creates the protected HumanTarget only AFTER staging
            // the player's pre-existing fleet. The imported native target
            // must remain anchored at the defended center, not be relocated.
            if slots.iter().any(|slot| slot.ship_type == HUMAN_TARGET_TYPE) {
                continue;
            }
            // Placement extents must describe the ACTUAL staged formation,
            // not the original ships' unrelated map positions. A squad
            // imported in a widely scattered state may compact here, while
            // the source adjusted formation of a compact squad may expand.
            let member_count = slots.len();
            for slot in &mut slots {
                slot.position = authored_placement_offset(
                    slot.ship_type, member_count, slot.source_offset,
                );
                if !slot.position.is_finite() {
                    return Err("Titania II fleet placement is nonfinite");
                }
            }
            let (formation_center, width, height) = geometry(&slots);
            let radius = width.max(height) * 0.5 + 8.0;
            if !radius.is_finite() || !formation_center.is_finite() {
                return Err("Titania II fleet size is nonfinite");
            }
            let chosen = place_at_center(radius, &occupied, &obstacles,
                bounds.min, bounds.max)
                .ok_or("Titania II cannot place player fleet inside the physical map")?;
            for slot in &mut slots {
                slot.position = chosen + slot.position - formation_center;
                if !slot.position.is_finite() {
                    return Err("Titania II fleet placement is nonfinite");
                }
            }
            let (actual_center, actual_width, actual_height) = geometry(&slots);
            occupied.push((actual_center, actual_width.max(actual_height) * 0.5 + 8.0));
            placements.extend(slots.iter().map(|slot| (slot.index, slot.position)));
        }
        for (index, position) in placements {
            let NativeAuthoredEntity::Ship(ship) = &mut self.entities[index] else {
                unreachable!("validated physical ship index");
            };
            ship.motion.position = position;
        }
        self.metadata.hivemind_starting_positions.as_mut()
            .expect("source starting positions validated")[index] = TITANIA_II_CENTER;
        Ok(())
    }
}
