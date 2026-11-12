//! Live source-grounded Hive Mind geometric refresh for CombatWorld.
//!
//! Kept adjacent to CombatWorld as a child module to access its canonical
//! ships, physics, obstacles and learned caches without exposing mutable
//! internals to unrelated game and training modules.

use super::*;

impl CombatWorld {
    /// Compute each faction's remaining physical obstacle discoveries once
    /// per refresh, not for every observer. No persistent duplicate cache:
    /// callers clear this work map before the one authoritative sight sweep.
    fn remaining_obstacle_vision_discoveries(&mut self, side: i32) -> u64 {
        if let Some(&count) = self.vision_unlearned_obstacles.get(&side) {
            return count;
        }
        let count = self.obstacles.iter()
            .zip(self.physics.obstacles()).filter(|(obstacle, body)| {
                if !obstacle.live() || !body.active { return false; }
                let kind = match obstacle.kind {
                    ObstacleKind::MiningAsteroid => DiscoveryKind::MiningAsteroid,
                    ObstacleKind::MapObject => DiscoveryKind::MapObject,
                    _ => DiscoveryKind::Obstacle,
                };
                !self.discovery.has(side, kind, obstacle.entity_id)
            }).count() as u64;
        self.vision_unlearned_obstacles.insert(side, count);
        count
    }

    /// Refresh Hive Mind learned discoveries with the SAME ship/obstacle
    /// collider geometry as native gameplay. The outer Level adapter owns
    /// real observer registration, HiveMindVision.enabled and effective
    /// sight ranges; these are not inferred from ship hulls or motion.
    ///
    /// Source: GameState.RefreshHiveMindMapObjectVision. Ship sightings are
    /// faction memory, not transient line-of-sight: newly observed targets
    /// remain learned after the observer moves or dies. Moving asteroids,
    /// static obstacles, and ordinary native MapObjects all use the same
    /// faction cache with distinct discovery categories. MapObjects use
    /// their primary collider; Fire Tank objects remain unsupported until
    /// their attributed explosion gameplay is available.
    ///
    /// Validate roster and dense physics storage BEFORE any discovery
    /// mutations. Missing observer data is an error, not a silent no-op.
    /// Each observer and target is tested at most once per pass; no heap
    /// allocations recur after the maximum observer count is reached.
    pub fn refresh_hivemind_vision(
        &mut self,
        observers: &[HiveMindObserver],
        setup_suppressed: bool,
    ) -> Result<VisionSweepCounts, VisionError> {
        let mut ignored = Vec::new();
        self.refresh_hivemind_vision_with_sightings(
            observers, setup_suppressed, &mut ignored,
        )
    }

    /// The game Level/reward owner receives the exact first discoveries in
    /// source observer/target traversal order. Both graphical and headless
    /// clients use this one detector. The caller owns/reuses the output
    /// buffer across fixed ticks, so no per-agent event allocation occurs.
    pub fn refresh_hivemind_vision_with_sightings(
        &mut self,
        observers: &[HiveMindObserver],
        setup_suppressed: bool,
        first_sightings: &mut Vec<VisionFirstSighting>,
    ) -> Result<VisionSweepCounts, VisionError> {
        first_sightings.clear();
        // The obstacle roster is stable throughout one exclusive vision
        // sweep, but new spawns and retirement can occur between sweeps.
        self.vision_unlearned_obstacles.clear();
        self.vision_obstacle_sightings.clear();
        self.vision_obstacle_offsets.clear();
        // Unity returns immediately during RL scene setup. The roster and
        // storage can legitimately be incomplete while ships are spawning,
        // so do not run visibility validation until setup has finished.
        if setup_suppressed {
            self.vision_live_enemy_sides.clear();
            let counts = VisionSweepCounts {
                completed_sweeps: 1,
                suppressed_sweeps: 1,
                ..VisionSweepCounts::default()
            };
            self.vision_measurements.merge(&counts);
            return Ok(counts);
        }
        if self.ships.len() != self.physics.ships().len() ||
            self.obstacles.len() != self.physics.obstacles().len() {
            return Err(VisionError::StorageMismatch);
        }
        for (ship, body) in self.ships.iter().zip(self.physics.ships()) {
            if ship.entity_id != body.id {
                return Err(VisionError::StorageMismatch);
            }
        }
        for (obstacle, body) in self.obstacles.iter().zip(self.physics.obstacles()) {
            if obstacle.entity_id != body.id {
                return Err(VisionError::StorageMismatch);
            }
        }
        self.vision_observer_ids.clear();
        for observer in observers {
            if observer.ship_id <= 0 {
                return Err(VisionError::InvalidObserverId(observer.ship_id));
            }
            if observer.effective_range < 0 {
                return Err(VisionError::InvalidEffectiveRange(observer.ship_id));
            }
            if !self.vision_observer_ids.insert(observer.ship_id) {
                return Err(VisionError::DuplicatedObserver(observer.ship_id));
            }
            let Some(&index) = self.ship_by_id.get(&observer.ship_id) else {
                return Err(VisionError::UnknownObserver(observer.ship_id));
            };
            let ship = &self.ships[index];
            let body = &self.physics.ships()[index];
            if ship.entity_id != body.id || ship.side <= 0 ||
                !body.motion.position.is_finite() {
                return Err(VisionError::InvalidObserverState(observer.ship_id));
            }
        }
        self.vision_live_enemy_sides.clear();
        let mut counts = VisionSweepCounts::default();
        counts.completed_sweeps = 1;
        // An observer's ship and obstacle discovery sets are independent.
        // Process all obstacles while the ONE grid owns obstacle hulls,
        // then all ship sightings while it owns ship hulls. Only genuine
        // first-obstacle sightings are staged for source-order emission:
        // memory is O(obstacles first discovered), not O(observers*candidates).
        // Per-observer end offsets preserve exact ship-before-obstacle and
        // observer-major notification order without another index or scan.
        for observer in observers {
            if observer.registered && observer.enabled &&
                observer.effective_range != 0 {
                let &index = self.ship_by_id.get(&observer.ship_id)
                    .expect("roster prevalidated");
                let ship = &self.ships[index];
                let body = &self.physics.ships()[index];
                if body.active && !ship.vitals.is_dead() {
                    let origin = body.motion.position;
                    let side = ship.side;
                    let range = observer.effective_range as f32;
                    let range_squared = range * range;
                    let pending = self.remaining_obstacle_vision_discoveries(side);
                    if pending > 0 {
                        let rebuilt = self.physics.circle_obstacle_vision_candidates(
                            origin, range, &mut self.vision_obstacle_candidates,
                        );
                        counts.obstacle_vision_grid_rebuilds += u64::from(rebuilt);
                        counts.obstacle_candidates += self.vision_obstacle_candidates.len() as u64;
                        self.vision_obstacle_candidates.sort_unstable();
                        counts.unlearned_obstacle_pairs += pending;
                        let mut considered = 0_u64;
                        let mut first_seen = 0_u64;
                        for &target in &self.vision_obstacle_candidates {
                            let obstacle = &self.obstacles[target];
                            if !obstacle.live() ||
                                !self.physics.obstacles()[target].active {
                                continue;
                            }
                            let kind = match obstacle.kind {
                                ObstacleKind::MiningAsteroid => DiscoveryKind::MiningAsteroid,
                                ObstacleKind::MapObject => DiscoveryKind::MapObject,
                                _ => DiscoveryKind::Obstacle,
                            };
                            let target_id = obstacle.entity_id;
                            if self.discovery.has(side, kind, target_id) {
                                continue;
                            }
                            considered += 1;
                            if self.physics.obstacles()[target].outside_vision_radius(
                                origin, range, kind == DiscoveryKind::MapObject,
                            ) {
                                counts.obstacle_radius_rejections += 1;
                                counts.aabb_rejections += 1;
                                continue;
                            }
                            let proximity = if kind == DiscoveryKind::MapObject {
                                self.physics.map_object_vision_proximity(
                                    target, origin, range_squared,
                                )
                            } else {
                                self.physics.obstacle_vision_proximity(
                                    target, origin, range_squared,
                                )
                            };
                            if counts.record_proximity(proximity) &&
                                self.discovery.record(side, kind, target_id)
                                    .expect("prevalidated side and entity ID")
                            {
                                first_seen += 1;
                                self.vision_obstacle_sightings.push(VisionFirstSighting {
                                    observer_id: observer.ship_id, side,
                                    target_id, kind,
                                });
                                match kind {
                                    DiscoveryKind::MiningAsteroid => {
                                        counts.first_mining_discoveries += 1;
                                    }
                                    DiscoveryKind::MapObject => {
                                        counts.first_map_object_discoveries += 1;
                                    }
                                    _ => { counts.first_obstacle_discoveries += 1; }
                                }
                            }
                        }
                        // A primary/clearance AABB outside the sight-square cannot
                        // contain a collider within the actual source sight-circle.
                        // No omitted obstacle was discovered, so every omitted
                        // unlearned pair is an equivalent cheap bounds rejection.
                        counts.aabb_rejections += pending.saturating_sub(considered);
                        let still_unlearned = pending.saturating_sub(first_seen);
                        self.vision_unlearned_obstacles.insert(side, still_unlearned);
                    }
                }
            }
            self.vision_obstacle_offsets.push(self.vision_obstacle_sightings.len());
        }

        for (observer_index, observer) in observers.iter().enumerate() {
            if !observer.registered || !observer.enabled ||
                observer.effective_range == 0 {
                continue;
            }
            let &index = self.ship_by_id.get(&observer.ship_id)
                .expect("roster prevalidated");
            let ship = &self.ships[index];
            let body = &self.physics.ships()[index];
            if !body.active || ship.vitals.is_dead() {
                continue;
            }
            let side = ship.side;
            let origin = body.motion.position;
            let range = observer.effective_range as f32;
            let range_squared = range * range;
            counts.active_observers += 1;

            // A distant enemy's collider AABB cannot intersect the sight
            // circle. Reuse the physical ship grid instead of scanning every
            // hull for every observer (quadratic in large sparse battles).
            // The grid includes out-of-bounds and oversized hulls, so no
            // valid edge-of-collider sighting is discarded.
            self.physics.circle_ship_contact_candidates(
                origin, range, &mut self.vision_ship_candidates,
            );
            // Unity first-sighting credit follows Level.State.Ships order.
            // Grid cells are not ordered that way. For dense queries avoid
            // a costly sort: visiting the complete roster is cheaper.
            if self.vision_ship_candidates.len().saturating_mul(2) >= self.ships.len() {
                self.vision_ship_candidates.clear();
                self.vision_ship_candidates.extend(0..self.ships.len());
            } else {
                self.vision_ship_candidates.sort_unstable();
            }
            let eligible_enemies = self.total_living_ships()
                .saturating_sub(self.living_ships_on_side(side)) as u64;
            let mut checked_enemies = 0u64;
            for &target in &self.vision_ship_candidates {
                let other = &self.ships[target];
                if other.side == side || other.vitals.is_dead() ||
                    !self.physics.ships()[target].active {
                    continue;
                }
                checked_enemies += 1;
                let target_id = other.entity_id;
                let proximity = self.physics.ship_vision_proximity(
                    target, origin, range_squared,
                );
                if counts.record_proximity(proximity) {
                    counts.visible_enemy_pairs += 1;
                    self.vision_live_enemy_sides.insert(side);
                    // Both bodies were already verified live, opposite-side
                    // and owned by this Level. Only the first faction-wide
                    // sighting triggers a new discovery event.
                    if self.discovery.record(side, DiscoveryKind::Ship, target_id)
                        .expect("prevalidated side and entity ID") {
                        counts.first_ship_discoveries += 1;
                        first_sightings.push(VisionFirstSighting {
                            observer_id: observer.ship_id, side,
                            target_id, kind: DiscoveryKind::Ship,
                        });
                    }
                }
            }
            // Skipped grid cells are provably outside the circle's AABB.
            // Retain the ORIGINAL total-pair and bounds-rejection counters
            // so before/after profiles remain comparable.
            counts.live_ship_pairs += eligible_enemies;
            counts.ship_pairs_tested += checked_enemies;
            counts.aabb_rejections += eligible_enemies.saturating_sub(checked_enemies);

            let from = if observer_index == 0 { 0 }
                else { self.vision_obstacle_offsets[observer_index - 1] };
            let through = self.vision_obstacle_offsets[observer_index];
            first_sightings.extend_from_slice(
                &self.vision_obstacle_sightings[from..through],
            );
        }
        self.vision_measurements.merge(&counts);
        Ok(counts)
    }


}
