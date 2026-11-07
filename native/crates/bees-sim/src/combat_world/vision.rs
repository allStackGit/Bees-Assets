//! Live source-grounded Hive Mind geometric refresh for CombatWorld.
//!
//! Kept adjacent to CombatWorld as a child module to access its canonical
//! ships, physics, obstacles and learned caches without exposing mutable
//! internals to unrelated game and training modules.

use super::*;

impl CombatWorld {
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
        for observer in observers {
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

            for target in 0..self.ships.len() {
                let other = &self.ships[target];
                if other.side == side || other.vitals.is_dead() ||
                    !self.physics.ships()[target].active {
                    continue;
                }
                let target_id = other.entity_id;
                counts.live_ship_pairs += 1;
                let proximity = self.physics.ships_mut()[target]
                    .vision_proximity(origin, range_squared);
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

            for target in 0..self.obstacles.len() {
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
                // Learned obstacle memory is sticky and no gameplay
                // feature needs transient obstacle-visibility pairs.
                // Skip expensive geometry checks once the side knows it.
                if self.discovery.has(side, kind, target_id) {
                    continue;
                }
                counts.unlearned_obstacle_pairs += 1;
                let proximity = if kind == DiscoveryKind::MapObject {
                    self.physics.obstacles_mut()[target]
                        .vision_proximity_primary(origin, range_squared)
                } else {
                    self.physics.obstacles_mut()[target]
                        .vision_proximity(origin, range_squared)
                };
                if counts.record_proximity(proximity) {
                    if self.discovery.record(side, kind, target_id)
                        .expect("prevalidated side and entity ID") {
                        first_sightings.push(VisionFirstSighting {
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
            }
        }
        self.vision_measurements.merge(&counts);
        Ok(counts)
    }


}
