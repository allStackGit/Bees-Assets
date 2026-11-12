//! Deferred deployment of authored, non-policy squads into a live Level.

use std::collections::{HashMap, HashSet, VecDeque};

use bees_gameplay::level_events::NativeLevelEventProcessor;
use bees_gameplay::level_host::NativeLevelActivationContext;
use bees_gameplay::level_rules::NativeLevelStepError;
use bees_gameplay::player_inputs::NativeLevelPlayerContext;
use bees_sim::{combat_world::CombatWorld, geometry::Vec2, squad_movement::{
    SquadFormationMember, authored_placement_offset, issue_squad_move,
}};

use crate::policy_only_metadata::NativePolicyOnlyMetadata;
use crate::scene_builder::{NativeAuthoredEntity, NativeAuthoredLevel, NativeAuthoredShip, NativeAuthoredSquad,
    install_ship_attachments, spawn_authored_ship};
use crate::ship_specials::NativeAuthoredShipSpecial;

pub struct NativeReinforcementPlan {
    pub after_seconds: f32,
    pub squad_item_ids: Vec<i64>,
    /// Source AddReinforcementSquads startingPosition. When supplied, the
    /// authored formation is placed here before its entry move.
    pub spawn: Option<Vec2>,
    pub entry: Option<Vec2>,
}

pub(crate) struct ReinforcementWave {
    tick: u64,
    entry: Option<Vec2>,
    squads: Vec<NativeAuthoredSquad>,
    ships: Vec<NativeAuthoredShip>,
}

impl ReinforcementWave {
    pub(crate) fn scheduled_tick(&self) -> u64 { self.tick }
    pub(crate) fn squad_count(&self) -> usize { self.squads.len() }
    pub(crate) fn ship_count(&self) -> usize { self.ships.len() }
}

/// Source LevelConstructor.PositionSquads with squadsAreReinforcements=true.
/// Each plan represents ONE source construction call, including any Carrier
/// child squads. The authored squad order controls alternating left/right
/// placement, independently of the imported scene entity-array order.
fn plan_reinforcement_wave(
    squads: &[&NativeAuthoredSquad],
    ships: &HashMap<i64, &NativeAuthoredShip>,
    spawn: Vec2,
    metadata: &NativePolicyOnlyMetadata,
) -> Result<HashMap<i64, Vec2>, &'static str> {
    // Unity sets every squad's initial formation before querying any
    // source GetPosition/GetWidth. Calculate first-pass bounds without
    // changing the authored ships; failures preserve the entire scene.
    let mut first_pass = Vec::with_capacity(squads.len());
    for squad in squads {
        let mut left = f32::INFINITY;
        let mut right = f32::NEG_INFINITY;
        let mut bottom = f32::INFINITY;
        let mut top = f32::NEG_INFINITY;
        for &ship_id in &squad.members {
            let ship = ships.get(&ship_id)
                .ok_or("Reinforcement squad has no physical ship")?;
            let offset = metadata.stored_formation_offset(ship_id)
                .ok_or("Reinforcement ship has no source formation offset")?;
            let adjusted = authored_placement_offset(
                ship.ship_type, squad.members.len(), offset,
            );
            let position = spawn + adjusted;
            let size = metadata.ship_catalog.sprite_size(ship.ship_type)
                .ok_or("Reinforcement ship has no authored sprite size")?;
            if !position.is_finite() || !size.is_finite() ||
                size.x <= 0.0 || size.y <= 0.0 {
                return Err("Reinforcement spawn and sprite geometry must be finite and positive");
            }
            let half = size * 0.5;
            left = left.min(position.x - half.x);
            right = right.max(position.x + half.x);
            bottom = bottom.min(position.y - half.y);
            top = top.max(position.y + half.y);
        }
        let center = Vec2::new((left + right) * 0.5, (bottom + top) * 0.5);
        let width = right - left;
        if !center.is_finite() || !width.is_finite() || width <= 0.0 {
            return Err("Reinforcement source formation bounds are invalid");
        }
        first_pass.push((center, width));
    }

    // Source PositionSquads disables level-edge wrapping for reinforcements.
    // Parent and Carrier descendants alternate left/right with a 10-unit
    // extent gap, using the first squad's actual centered Y coordinate.
    let mut first_squad_center = Vec2::ZERO;
    let mut previous_left_width = 0.0f32;
    let mut previous_right_width = 0.0f32;
    let mut left_span = 0.0f32;
    let mut right_span = 0.0f32;
    let mut placements = HashMap::with_capacity(
        squads.iter().map(|squad| squad.members.len()).sum(),
    );
    for (ordinal, squad) in squads.iter().enumerate() {
        let (initial_center, width) = first_pass[ordinal];
        let mut position = initial_center;
        if ordinal == 0 {
            previous_left_width = width;
            previous_right_width = width;
        } else {
            position.y = first_squad_center.y;
            if ordinal % 2 == 0 {
                right_span += width * 0.5 + 10.0 + previous_right_width * 0.5;
                position.x += right_span;
                previous_right_width = width;
            } else {
                left_span += width * 0.5 + 10.0 + previous_left_width * 0.5;
                position.x -= left_span;
                previous_left_width = width;
            }
        }
        if !position.is_finite() {
            return Err("Reinforcement second placement overflowed world coordinates");
        }
        // The second SetStartingPosition overwrites the first-pass pose,
        // but still uses the adjusted placement offset. Squad.Move later
        // reads the original, unadjusted formation offset.
        for &ship_id in &squad.members {
            let ship = ships.get(&ship_id)
                .ok_or("Reinforcement squad has no physical ship")?;
            let offset = metadata.stored_formation_offset(ship_id)
                .ok_or("Reinforcement ship has no source formation offset")?;
            let adjusted = authored_placement_offset(
                ship.ship_type, squad.members.len(), offset,
            );
            let physical_position = position + adjusted;
            if !physical_position.is_finite() {
                return Err("Reinforcement second placement overflowed world coordinates");
            }
            placements.insert(ship_id, physical_position);
        }
        if ordinal == 0 {
            // Unity's GetPosition includes the authored sprite-bounds bias.
            first_squad_center = position + (initial_center - spawn);
            if !first_squad_center.is_finite() {
                return Err("Reinforcement first squad center is invalid");
            }
        }
    }
    Ok(placements)
}

impl<A> NativeAuthoredLevel<NativePolicyOnlyMetadata, A> {
    pub(crate) fn stage_reinforcements(&mut self, plans: Vec<NativeReinforcementPlan>, dt: f32)
        -> Result<(), &'static str>
    {
        if plans.is_empty() { return Ok(()); }
        // Source SetStartingPosition may use Pathfinder to search a nearby
        // formation center or separate slots in obstacle-bearing maps.
        // A requested spawn alone cannot reproduce that decision. Reject
        // such plans before consuming any authored squads or entities.
        if self.metadata.player_formation_options().has_obstacles &&
            plans.iter().any(|plan| plan.spawn.is_some()) {
            return Err("Obstacle-bearing reinforcement spawns need captured physical ship positions (omit spawn)");
        }
        let mut squad_waves = HashMap::new();
        let mut waves = Vec::with_capacity(plans.len());
        let mut spawn_centers = Vec::with_capacity(plans.len());
        let mut planned_squad_order = Vec::with_capacity(plans.len());
        let mut previous = 0.0;
        // Equal-time waves are valid: Titania II stages two distinct
        // formations at 375 seconds with different entry destinations.
        // Preserve authored order for a common due tick.
        for plan in plans {
            let tick = (f64::from(plan.after_seconds) / f64::from(dt)).floor() + 1.0;
            if !plan.after_seconds.is_finite() || plan.after_seconds <= 0.0 ||
                plan.after_seconds < previous ||
                !tick.is_finite() || tick >= u64::MAX as f64 || plan.squad_item_ids.is_empty() ||
                plan.spawn.is_some_and(|spawn| !spawn.is_finite()) ||
                plan.entry.is_some_and(|entry| !entry.is_finite())
            {
                return Err("Reinforcements need positive nondecreasing times, nonempty squads, and finite spawn/entry coordinates");
            }
            // Multiple IDs within a single plan share the source
            // LevelConstructor.PositionSquads call, including Carrier
            // children. Their authored order determines physical spacing.
            for &id in &plan.squad_item_ids {
                if squad_waves.insert(id, waves.len()).is_some() {
                    return Err("A squad may arrive in only one reinforcement wave");
                }
            }
            planned_squad_order.push(plan.squad_item_ids);
            previous = plan.after_seconds;
            spawn_centers.push(plan.spawn);
            // Unity LevelConstructor.PositionSquads skips Squad.Move when
            // moveToPoint == Vector2.zero. That is a source sentinel, not a
            // physical point order; use Unity's epsilon-aware equality.
            let entry = plan.entry.filter(|point| !point.unity_equal(Vec2::ZERO));
            waves.push(ReinforcementWave {
                tick: tick as u64, entry,
                squads: Vec::new(), ships: Vec::new(),
            });
        }
        // Validate deferred membership and physical ownership BEFORE taking
        // any authored scene objects. Partial extraction on a malformed
        // later wave would otherwise make this Level unusable on retry.
        let mut source_squads = HashMap::with_capacity(squad_waves.len());
        for squad in &self.squads {
            if squad_waves.contains_key(&squad.owner.squad_id) &&
                source_squads.insert(squad.owner.squad_id, squad).is_some() {
                return Err("Reinforcements contain duplicate source squad identities");
            }
        }
        if source_squads.len() != squad_waves.len() {
            return Err("Reinforcements reference an unknown source squad item ID");
        }
        let mut expected_ships = HashMap::new();
        for (wave_idx, ids) in planned_squad_order.iter().enumerate() {
            for id in ids {
                let squad = source_squads[id];
                if squad.members.is_empty() || squad.side <= 0 || !squad.owner.valid() {
                    return Err("Reinforcement squads need valid owners and nonempty membership");
                }
                for &ship_id in &squad.members {
                    if ship_id <= 0 || expected_ships.insert(ship_id, (wave_idx, squad.side)).is_some() {
                        return Err("Reinforcement ship membership must be unique and positive");
                    }
                }
            }
        }
        let mut source_ships = HashMap::with_capacity(expected_ships.len());
        for entity in &self.entities {
            let NativeAuthoredEntity::Ship(ship) = entity else { continue };
            let wave = expected_ships.get(&ship.entity_id).copied();
            let striker_carrier = match &ship.special {
                Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) => carrier.carrier_id,
                _ => None,
            };
            if [ship.drone_carrier_id, striker_carrier].into_iter().flatten().any(|id|
                expected_ships.get(&id).map(|&(wave, _)| wave) != wave.map(|(wave, _)| wave)
            ) {
                return Err("Reinforcement carrier and child must deploy together");
            }
            let Some((wave_idx, expected_side)) = wave else { continue };
            if ship.side != expected_side || source_ships.insert(ship.entity_id, ship).is_some() {
                return Err("Reinforcement physical ship identities or sides conflict with squads");
            }
            if ship.policy.is_some() || ship.motion.is_policy_controlled {
                return Err("Reinforcements cannot reserve a live policy controller");
            }
            if waves[wave_idx].entry.is_some() &&
                (self.metadata.stored_formation_offset(ship.entity_id)
                    .is_none_or(|offset| !offset.is_finite()) ||
                 self.metadata.current_path_clearance(ship.entity_id)
                    .is_none_or(|clearance| clearance < 0)) {
                return Err("Reinforcement entry orders need finite source offsets and nonnegative clearance");
            }
        }
        if source_ships.len() != expected_ships.len() {
            return Err("Reinforcement squads need one physical ship per member");
        }
        // One wave represents exactly one source constructor invocation.
        // Validate Carrier descendants EVEN when the wave's physical
        // positions come from a later capture and spawn is omitted.
        for ids in &planned_squad_order {
            if ids.len() < 2 { continue; }
            let root = source_squads[&ids[0]];
            let parents: HashSet<i64> = root.members.iter().copied().filter(|id|
                source_ships.get(id).is_some_and(|ship| ship.ship_type == 5)
            ).collect();
            for id in ids.iter().skip(1) {
                let child = source_squads[id];
                if child.side != root.side {
                    return Err("Carrier child reinforcement squad has a different side from its source squad");
                }
                let mut parent = None;
                for ship_id in &child.members {
                    let ship = source_ships[ship_id];
                    let carrier = match ship.ship_type {
                        8 => ship.drone_carrier_id,
                        19 => match &ship.special {
                            Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) => carrier.carrier_id,
                            _ => None,
                        },
                        _ => None,
                    }.ok_or("Multi-squad reinforcement spawn requires source-linked Carrier children")?;
                    if !parents.contains(&carrier) || parent.is_some_and(|id| id != carrier) {
                        return Err("Reinforcement Carrier child squad must share a parent in the primary squad");
                    }
                    parent = Some(carrier);
                }
            }
        }
        // Resolve ALL wave positions before extracting the first deferred
        // ship or squad. The subsequent commit phase cannot fail on late
        // formation geometry or Carrier group membership.
        let mut planned_positions = HashMap::new();
        for (wave_idx, ids) in planned_squad_order.iter().enumerate() {
            let Some(spawn) = spawn_centers[wave_idx] else { continue };
            let squads: Vec<&NativeAuthoredSquad> = ids.iter()
                .map(|id| source_squads[id]).collect();
            planned_positions.extend(plan_reinforcement_wave(
                &squads, &source_ships, spawn, &self.metadata,
            )?);
        }
        let mut selected_squads = HashMap::new();
        let mut initial_squads = Vec::new();
        for squad in std::mem::take(&mut self.squads) {
            if squad_waves.remove(&squad.owner.squad_id).is_some() {
                selected_squads.insert(squad.owner.squad_id, squad);
            } else { initial_squads.push(squad); }
        }
        debug_assert!(squad_waves.is_empty());
        // The source LevelConstructor uses the requested squad list order
        // when positioning and registering each wave. The initial scene's
        // squad array can have a different order after merged captures.
        for (wave_idx, ids) in planned_squad_order.into_iter().enumerate() {
            for id in ids {
                let squad = selected_squads.remove(&id)
                    .expect("source reinforcement squad preflighted");
                waves[wave_idx].squads.push(squad);
            }
        }
        let mut initial_entities = Vec::new();
        for entity in std::mem::take(&mut self.entities) {
            let NativeAuthoredEntity::Ship(mut ship) = entity else { initial_entities.push(entity); continue };
            if let Some(&(wave, _)) = expected_ships.get(&ship.entity_id) {
                if let Some(&position) = planned_positions.get(&ship.entity_id) {
                    ship.motion.position = position;
                }
                self.reserved_ship_ids.push(ship.entity_id);
                waves[wave].ships.push(ship);
            } else {
                initial_entities.push(NativeAuthoredEntity::Ship(ship));
            }
        }
        self.squads = initial_squads;
        self.entities = initial_entities;
        // Membership, source links, entry eligibility and two-pass
        // placement have all been validated against the unmodified scene.
        // This phase only preserves canonical physical creation order.
        for wave in &mut waves {
            wave.ships.sort_unstable_by_key(|ship| ship.entity_id);
        }
        self.metadata.reinforcements = VecDeque::from(waves);
        Ok(())
    }
}

impl NativePolicyOnlyMetadata {
    pub(crate) fn activate_reinforcements(
        &mut self, world: &mut CombatWorld, events: &mut NativeLevelEventProcessor, tick: u64,
    ) -> Result<(), NativeLevelStepError> {
        // Unity's obstacle-aware Squad.Move checks the current Pathfinder.
        // The normal native command path refreshes live clearance immediately
        // before planning, but deferred waves can arrive after the combat step
        // has moved or destroyed collision asteroids. Refresh once per
        // arrival tick, only when a due wave has an obstacle-aware entry move.
        let mut navigation_refreshed = false;
        while self.reinforcements.front().is_some_and(|wave| wave.tick <= tick) {
            let wave = self.reinforcements.pop_front().expect("due wave");
            let (ships, squads) = (wave.ships.len(), wave.squads.len());
            let invalid = |error| NativeLevelStepError::Reinforcements(error);
            let mut attachments = Vec::new();
            for ship in wave.ships {
                attachments.extend(spawn_authored_ship(&self.ship_catalog, world, ship, true)
                    .map_err(|error| invalid(format!("{error:?}")))?);
            }
            install_ship_attachments(world, attachments).map_err(|error| invalid(format!("{error:?}")))?;
            let options = self.player_formation_options();
            if wave.entry.is_some() && options.has_obstacles && !navigation_refreshed {
                world.refresh_world_navigation(
                    &mut self.navigation, true, self.activate_collision_asteroids,
                ).map_err(|error| invalid(format!("Reinforcement navigation: {error:?}")))?;
                navigation_refreshed = true;
            }
            for squad in wave.squads {
                if let Some(entry) = wave.entry {
                    let members = squad.members.iter().map(|&ship_id| {
                        let missing = || invalid(format!("Missing reinforcement formation for ship {ship_id}"));
                        Ok(SquadFormationMember {
                            ship_id,
                            offset_from_center: self.stored_formation_offset(ship_id).ok_or_else(missing)?,
                            clearance: self.current_path_clearance(ship_id).ok_or_else(missing)?,
                            mobile: world.ship_body(ship_id).ok_or_else(missing)?.motion.mobile,
                        })
                    }).collect::<Result<Vec<_>, NativeLevelStepError>>()?;
                    if members.iter().any(|member| member.mobile) {
                        let plan = issue_squad_move(world, &self.navigation, &members, entry, options)
                            .map_err(|error| invalid(format!("{error:?}")))?;
                        if plan.is_none() { return Err(invalid(format!("Reinforcement squad {} has no reachable entry", squad.owner.squad_id))); }
                    }
                }
                squad.register(world, events.roster_mut()).map_err(|error| invalid(format!("{error:?}")))?;
            }
            events.record_reinforcements(tick, ships, squads)?;
        }
        Ok(())
    }
}
