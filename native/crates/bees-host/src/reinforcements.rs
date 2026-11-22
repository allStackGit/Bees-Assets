//! Deferred deployment of authored squads and physical policy agents into a live Level.

use std::collections::{HashMap, HashSet, VecDeque};

use bees_hivemind::session::SquadLifetime;
use bees_gameplay::level_events::NativeLevelEventProcessor;
use bees_gameplay::campaign_dialogue::CampaignDialogueSpec;
use bees_gameplay::level_host::NativeLevelActivationContext;
use bees_gameplay::level_rules::NativeLevelStepError;
use bees_gameplay::player_inputs::NativeLevelPlayerContext;
use bees_sim::{combat_world::CombatWorld, geometry::Vec2, squad_movement::{
    SquadFormationMember, authored_placement_offset, issue_squad_move,
}};

use crate::policy_only_metadata::{
    NativePolicyOnlyMetadata, NativeMissionCue, NativeMissionEvent,
};
use crate::scene_builder::{NativeAuthoredEntity, NativeAuthoredLevel, NativeAuthoredShip, NativeAuthoredSquad,
    install_ship_attachments, spawn_authored_ship};
use crate::ship_specials::NativeAuthoredShipSpecial;

/// Exactly one source-owned admission gate for a deferred wave.
#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub enum NativeReinforcementActivation {
    AfterSeconds(f32),
    OnCampaignCue(NativeMissionCue),
    OnShipDestroyed(i64),
    OnShipDamaged(i64),
    OnShipExtracted(i64),
    OnShipWarped(i64),
    OnMapObjectDestroyed(i64),
    OnMiningAsteroidDepleted(i64),
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeReinforcementPlan {
    pub activation: NativeReinforcementActivation,
    pub squad_item_ids: Vec<i64>,
    /// Source AddReinforcementSquads startingPosition. When supplied, the
    /// authored formation is placed here before its entry move.
    pub spawn: Option<Vec2>,
    pub entry: Option<Vec2>,
}

#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
enum ReinforcementDue {
    Tick(u64),
    Event(NativeMissionEvent),
}

/// Source-authored deferred wave identity. Checkpoints retain the
/// remainder of the queue, not a second copy of ship prefabs.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct ReinforcementWaveIdentity {
    due: ReinforcementDue,
    entry: Option<Vec2>,
    squads: Vec<(SquadLifetime, i32, Vec<i64>)>,
    ships: Vec<(i64, u8, i32)>,
    policy_agents: usize,
    group_agents: Vec<(i64, i32, u64)>,
}

pub(crate) struct ReinforcementWave {
    due: ReinforcementDue,
    entry: Option<Vec2>,
    squads: Vec<NativeAuthoredSquad>,
    ships: Vec<NativeAuthoredShip>,
    /// Actual model-controlled ships, whether or not MA-POCA is enabled.
    policy_agents: usize,
    /// Only training groups register separate source reward identities.
    group_agents: Vec<(i64, i32, u64)>,
}

/// Cache only the earliest timed wakeup and which source event classes
/// remain outstanding. This is derived solely from the authoritative queue,
/// rebuilt when that queue changes, and never schedules separate gameplay.
/// Hundreds of deferred squads can wait for minutes without an O(waves)
/// scan on every simulation tick in every training arena.
#[derive(Default, Clone, Copy)]
pub(crate) struct ReinforcementWakeup {
    pub next_tick: Option<u64>,
    campaign_event: bool,
    ship_death_event: bool,
    ship_damage_event: bool,
    ship_exit_event: bool,
    map_object_death_event: bool,
    mining_depletion_event: bool,
}

impl ReinforcementWakeup {
    pub(crate) fn from_waves(waves: &VecDeque<ReinforcementWave>) -> Self {
        let mut state = Self::default();
        for wave in waves {
            match wave.due {
                ReinforcementDue::Tick(tick) =>
                    state.next_tick = Some(state.next_tick.map_or(tick, |old| old.min(tick))),
                ReinforcementDue::Event(NativeMissionEvent::CampaignCue(_)) =>
                    state.campaign_event = true,
                ReinforcementDue::Event(NativeMissionEvent::ShipDestroyed(_)) =>
                    state.ship_death_event = true,
                ReinforcementDue::Event(NativeMissionEvent::ShipDamaged(_)) =>
                    state.ship_damage_event = true,
                ReinforcementDue::Event(NativeMissionEvent::ShipExtracted(_) |
                    NativeMissionEvent::ShipWarped(_)) =>
                    state.ship_exit_event = true,
                ReinforcementDue::Event(NativeMissionEvent::MapObjectDestroyed(_)) =>
                    state.map_object_death_event = true,
                ReinforcementDue::Event(NativeMissionEvent::MiningAsteroidDepleted(_)) =>
                    state.mining_depletion_event = true,
            }
        }
        state
    }

    fn may_be_due(&self, tick: u64, world: &CombatWorld, events: &NativeLevelEventProcessor) -> bool {
        if self.next_tick.is_some_and(|at| tick >= at) ||
            (self.campaign_event && events.campaign_cue_fired(tick, |_| true))
        {
            return true;
        }
        if !(self.ship_death_event || self.ship_damage_event || self.ship_exit_event ||
            self.map_object_death_event || self.mining_depletion_event)
        {
            return false;
        }
        // One canonical event pass, regardless of how many physical
        // trigger classes are armed. Large battles can destroy ships,
        // props and ore on the same fixed tick, but an idle simulation
        // still does zero event work before its next scheduled wakeup.
        world.events().iter().any(|event| match event {
            bees_sim::combat_world::CombatEvent::ShipDied { end_kill: false, .. } =>
                self.ship_death_event,
            bees_sim::combat_world::CombatEvent::Hit(hit) =>
                self.ship_damage_event && hit.delta.applied_damage > 0,
            bees_sim::combat_world::CombatEvent::UnattributedDamage { delta, .. } =>
                self.ship_damage_event && delta.applied_damage > 0,
            bees_sim::combat_world::CombatEvent::ShipExtracted { .. } |
            bees_sim::combat_world::CombatEvent::ShipWarped { .. } =>
                self.ship_exit_event,
            bees_sim::combat_world::CombatEvent::MapObjectDestroyed { .. } =>
                self.map_object_death_event,
            bees_sim::combat_world::CombatEvent::MiningAsteroidDepleted { .. } =>
                self.mining_depletion_event,
            _ => false,
        })
    }
}

impl ReinforcementWave {
    /// True only for Pluto II's actual deferred combat deployment. The
    /// checkpoint stores the remaining queue, so its consumed prefix is
    /// evidence that the original tutorial released the Bee wave.
    pub(crate) fn is_pluto_combat_wave(&self) -> bool {
        matches!(self.due, ReinforcementDue::Event(
            NativeMissionEvent::CampaignCue(
                NativeMissionCue::PlutoReinforcementsCombatReady
            )
        ))
    }

    pub(crate) fn identity(&self) -> ReinforcementWaveIdentity {
        ReinforcementWaveIdentity {
            due: self.due,
            entry: self.entry,
            squads: self.squads.iter().map(|squad|
                (squad.owner, squad.side, squad.members.clone())
            ).collect(),
            ships: self.ships.iter().map(|ship|
                (ship.entity_id, ship.ship_type, ship.side)
            ).collect(),
            policy_agents: self.policy_agents,
            group_agents: self.group_agents.clone(),
        }
    }

    pub(crate) fn scheduled_tick(&self) -> Option<u64> {
        match self.due {
            ReinforcementDue::Tick(tick) => Some(tick),
            ReinforcementDue::Event(_) => None,
        }
    }

    fn is_due(
        &self, tick: u64, world: &CombatWorld,
        events: &NativeLevelEventProcessor,
    ) -> bool {
        match self.due {
            ReinforcementDue::Tick(due) => tick >= due,
            ReinforcementDue::Event(expected) => expected.fired(tick, world, events),
        }
    }

    pub(crate) fn is_campaign_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::CampaignCue(_))
        )
    }

    pub(crate) fn is_ship_death_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::ShipDestroyed(_))
        )
    }
    pub(crate) fn is_ship_damage_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::ShipDamaged(_))
        )
    }
    pub(crate) fn is_ship_exit_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::ShipExtracted(_) |
                NativeMissionEvent::ShipWarped(_))
        )
    }
    pub(crate) fn is_map_object_death_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::MapObjectDestroyed(_))
        )
    }
    pub(crate) fn is_mining_depletion_triggered(&self) -> bool {
        matches!(self.due,
            ReinforcementDue::Event(NativeMissionEvent::MiningAsteroidDepleted(_))
        )
    }
    pub(crate) fn squad_count(&self) -> usize { self.squads.len() }
    pub(crate) fn ship_count(&self) -> usize { self.ships.len() }
}

/// Source AddReinforcementSquads calls SpawnShipsAndSquads separately
/// for EACH independent SavedSquad. Only Carrier children join their
/// parent's constructor call, where PositionSquads spreads them apart.
/// Preserve that distinction rather than spreading unrelated Uranus
/// reinforcements as if they belonged to one giant Carrier fleet.
fn plan_reinforcement_wave(
    squads: &[&NativeAuthoredSquad],
    ships: &HashMap<i64, &NativeAuthoredShip>,
    spawn: Vec2,
    metadata: &NativePolicyOnlyMetadata,
) -> Result<HashMap<i64, Vec2>, &'static str> {
    let mut construction_groups: Vec<Vec<&NativeAuthoredSquad>> = Vec::new();
    let mut carrier_group = HashMap::new();
    for &squad in squads {
        let mut parent = None;
        let mut child_count = 0;
        for id in &squad.members {
            let ship = ships.get(id)
                .ok_or("Reinforcement source squad lacks its physical ship")?;
            if let Some(carrier) = reinforcement_carrier_parent(ship) {
                if parent.is_some_and(|previous| previous != carrier) {
                    return Err("Reinforcement child squad mixes Carrier parents");
                }
                parent = Some(carrier);
                child_count += 1;
            }
        }
        if child_count != 0 && child_count != squad.members.len() {
            return Err("Reinforcement Carrier child squad mixes unrelated ships");
        }
        if let Some(parent) = parent {
            let &group = carrier_group.get(&parent)
                .ok_or("Reinforcement Carrier child precedes or lacks its parent")?;
            construction_groups[group].push(squad);
        } else {
            let group = construction_groups.len();
            for id in &squad.members {
                if ships[id].ship_type == 5 {
                    carrier_group.insert(*id, group);
                }
            }
            construction_groups.push(vec![squad]);
        }
    }
    let mut placements = HashMap::new();
    for group in construction_groups {
        placements.extend(plan_reinforcement_constructor(
            &group, ships, spawn, metadata,
        )?);
    }
    Ok(placements)
}

/// Unity Squad.SetOffscreenStartingPosition bypasses its ordinary
/// obstacle-aware starting-position search. Every SavedSquad and every
/// generated Carrier child uses the SAME authored offscreen center and
/// its own adjusted offsets, without constructor horizontal spacing.
fn plan_offscreen_reinforcement(
    squads: &[&NativeAuthoredSquad],
    ships: &HashMap<i64, &NativeAuthoredShip>,
    spawn: Vec2,
    metadata: &NativePolicyOnlyMetadata,
) -> Result<HashMap<i64, Vec2>, &'static str> {
    let mut positions = HashMap::new();
    for squad in squads {
        for &ship_id in &squad.members {
            let ship = ships.get(&ship_id)
                .ok_or("Offscreen reinforcement lacks a physical ship")?;
            let offset = metadata.stored_formation_offset(ship_id)
                .ok_or("Offscreen reinforcement lacks its source offset")?;
            if !offset.is_finite() {
                return Err("Offscreen reinforcement has a nonfinite formation offset");
            }
            let position = spawn + authored_placement_offset(
                ship.ship_type, squad.members.len(), offset,
            );
            if !position.is_finite() {
                return Err("Offscreen reinforcement position overflowed");
            }
            positions.insert(ship_id, position);
        }
    }
    Ok(positions)
}

/// Unity Squad.TryFindNearestFormationCenter uses the authored Pathfinder
/// clearance map at Scale*2 spacing. Keep the adjusted formation intact
/// while searching, rather than moving individual hulls with an unrelated
/// collision approximation. Both game and policy reinforcements use this
/// same one-time scene staging operation.
fn place_reinforcement_formation(
    squad: &NativeAuthoredSquad,
    ships: &HashMap<i64, &NativeAuthoredShip>,
    positions: &mut HashMap<i64, Vec2>,
    metadata: &NativePolicyOnlyMetadata,
    bounds: bees_sim::collision::Aabb,
) -> Result<(), &'static str> {
    let mut members = Vec::with_capacity(squad.members.len());
    for &id in &squad.members {
        let ship = ships.get(&id)
            .ok_or("Reinforcement formation lacks its source ship")?;
        let offset = metadata.stored_formation_offset(id)
            .ok_or("Reinforcement formation lacks its source offset")?;
        let clearance = metadata.current_path_clearance(id)
            .ok_or("Reinforcement formation lacks source path clearance")?;
        let adjusted = authored_placement_offset(
            ship.ship_type, squad.members.len(), offset,
        );
        if !adjusted.is_finite() || clearance < 0 {
            return Err("Reinforcement formation has invalid authored clearance");
        }
        members.push((id, adjusted, clearance));
    }
    let Some(&(first_id, first_offset, _)) = members.first() else {
        return Err("Reinforcement formation has no ships");
    };
    let requested_center = *positions.get(&first_id)
        .ok_or("Reinforcement formation has no staged position")? - first_offset;
    let frame = metadata.navigation.map_frame();
    let clearance = metadata.navigation.clearance();
    let fits = |center: Vec2| {
        center.is_finite() && members.iter().all(|&(_, offset, required)| {
            let slot = center + offset;
            slot.is_finite() &&
                slot.x >= bounds.min.x && slot.x <= bounds.max.x &&
                slot.y >= bounds.min.y && slot.y <= bounds.max.y &&
                clearance.can_occupy_cell(frame.world_to_level(slot), required)
        })
    };
    let mut selected = fits(requested_center).then_some(requested_center);
    if selected.is_none() {
        // Source x-major/y-minor perimeter ordering, first valid radius
        // and minimum squared displacement within that radius.
        let extent = (bounds.max.x - bounds.min.x)
            .max(bounds.max.y - bounds.min.y);
        let max_radius = (extent / (bees_sim::navigation::NAVIGATION_CELL_SCALE * 2.0))
            .ceil() as i32;
        let step = bees_sim::navigation::NAVIGATION_CELL_SCALE * 2.0;
        for ring in 1..=max_radius {
            let mut best: Option<(Vec2, f32)> = None;
            for x in -ring..=ring {
                for y in -ring..=ring {
                    if x.abs().max(y.abs()) != ring { continue; }
                    let candidate = requested_center +
                        Vec2::new(x as f32 * step, y as f32 * step);
                    if !fits(candidate) { continue; }
                    let distance = (candidate - requested_center).length_squared();
                    if best.is_none_or(|(_, prior)| distance < prior) {
                        best = Some((candidate, distance));
                    }
                }
            }
            if let Some((center, _)) = best {
                selected = Some(center);
                break;
            }
        }
    }
    if let Some(center) = selected {
        for (id, offset, _) in members {
            positions.insert(id, center + offset);
        }
        return Ok(());
    }

    // Source Squad.PlaceFormationSlotsIndividually is the final fallback
    // when no translated formation fits. Search each ship's original slot
    // in authored member order, respecting previously placed hull extents.
    // These are staging coordinates only; movement and collision remain in
    // the authoritative CombatWorld after admission.
    let mut placed: Vec<(i64, Vec2, Vec2)> = Vec::with_capacity(members.len());
    for &(id, offset, required) in &members {
        let ship = ships[&id];
        let size = metadata.ship_catalog.sprite_size(ship.ship_type)
            .ok_or("Reinforcement fallback has no source sprite size")?;
        if !size.is_finite() || size.x <= 0.0 || size.y <= 0.0 {
            return Err("Reinforcement fallback ship dimensions are invalid");
        }
        let half = size * 0.5;
        let requested = requested_center + offset;
        let valid = |position: Vec2| {
            position.is_finite() &&
            position.x >= bounds.min.x && position.x <= bounds.max.x &&
            position.y >= bounds.min.y && position.y <= bounds.max.y &&
            clearance.can_occupy_cell(frame.world_to_level(position), required) &&
            !placed.iter().any(|&(_, previous, previous_half)| {
                (position.x - previous.x).abs() < half.x + previous_half.x &&
                (position.y - previous.y).abs() < half.y + previous_half.y
            })
        };
        let mut chosen = valid(requested).then_some(requested);
        if chosen.is_none() {
            let step = bees_sim::navigation::NAVIGATION_CELL_SCALE;
            for ring in 1i32..=64 {
                let mut best: Option<(Vec2, f32)> = None;
                for x in -ring..=ring {
                    for y in -ring..=ring {
                        if x.abs().max(y.abs()) != ring { continue; }
                        let candidate = requested +
                            Vec2::new(x as f32 * step, y as f32 * step);
                        if !valid(candidate) { continue; }
                        let distance = (candidate - requested).length_squared();
                        if best.is_none_or(|(_, previous)| distance < previous) {
                            best = Some((candidate, distance));
                        }
                    }
                }
                if let Some((point, _)) = best {
                    chosen = Some(point);
                    break;
                }
            }
        }
        // The source nearest-destination fallback deliberately ignores
        // previously placed hulls if every nearby nonoverlapping slot is
        // blocked. It still requires a walkable Pathfinder destination.
        if chosen.is_none() {
            let bounded = Vec2::new(
                requested.x.clamp(bounds.min.x, bounds.max.x),
                requested.y.clamp(bounds.min.y, bounds.max.y),
            );
            chosen = clearance.nearest_walkable_destination(
                frame.world_to_level(bounded), required,
            ).map(|local| frame.level_to_world(local))
                .filter(|point| point.x >= bounds.min.x &&
                    point.x <= bounds.max.x && point.y >= bounds.min.y &&
                    point.y <= bounds.max.y);
        }
        // Source FindNearestIndividualFormationSlot returns the requested
        // position if no valid destination exists anywhere on the map.
        // The canonical physical world then handles any actual contact.
        let point = chosen.unwrap_or(requested);
        if !point.is_finite() {
            return Err("Reinforcement fallback position is nonfinite");
        }
        placed.push((id, point, half));
    }
    for (id, point, _) in placed {
        positions.insert(id, point);
    }
    Ok(())
}

fn reinforcement_carrier_parent(ship: &NativeAuthoredShip) -> Option<i64> {
    match ship.ship_type {
        8 => ship.drone_carrier_id,
        19 => match &ship.special {
            Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) =>
                carrier.carrier_id,
            _ => None,
        },
        _ => None,
    }
}

/// PositionSquads is called once per original constructor invocation:
/// the primary SavedSquad and only its generated Carrier descendants.
/// Within that one family the authored order controls left/right spacing.
fn plan_reinforcement_constructor(
    squads: &[&NativeAuthoredSquad],
    ships: &HashMap<i64, &NativeAuthoredShip>,
    spawn: Vec2,
    metadata: &NativePolicyOnlyMetadata,
) -> Result<HashMap<i64, Vec2>, &'static str> {
    // Unity sets every squad's initial formation before querying any
    // source GetPosition/GetWidth. Calculate first-pass bounds without
    // changing the authored ships; failures preserve the entire scene.
    let mut first_pass = Vec::with_capacity(squads.len());
    let obstacle_aware = metadata.player_formation_options().has_obstacles;
    for squad in squads {
        let mut initial_positions = HashMap::with_capacity(squad.members.len());
        for &ship_id in &squad.members {
            let ship = ships.get(&ship_id)
                .ok_or("Reinforcement squad has no physical ship")?;
            let offset = metadata.stored_formation_offset(ship_id)
                .ok_or("Reinforcement ship has no source formation offset")?;
            let adjusted = authored_placement_offset(
                ship.ship_type, squad.members.len(), offset,
            );
            let position = spawn + adjusted;
            if !position.is_finite() {
                return Err("Reinforcement first placement overflowed world coordinates");
            }
            initial_positions.insert(ship_id, position);
        }
        // PositionSquads first calls SetStartingPosition for every squad,
        // before calculating its widths or moving Carrier child formations.
        if obstacle_aware && squad.members.iter().all(|&id|
            metadata.current_path_clearance(id).is_some()
        ) {
            place_reinforcement_formation(
                squad, ships, &mut initial_positions, metadata, metadata.bounds,
            )?;
        }
        let mut left = f32::INFINITY;
        let mut right = f32::NEG_INFINITY;
        let mut bottom = f32::INFINITY;
        let mut top = f32::NEG_INFINITY;
        for &ship_id in &squad.members {
            let ship = ships[&ship_id];
            let position = initial_positions[&ship_id];
            let size = metadata.ship_catalog.sprite_size(ship.ship_type)
                .ok_or("Reinforcement ship has no authored sprite size")?;
            if !size.is_finite() || size.x <= 0.0 || size.y <= 0.0 {
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
        // Second SetStartingPosition searches once more at the actual
        // centered/alternating constructor location. In the original Level
        // this can move the first family, which also changes the Y origin
        // used to stage all subsequent Carrier children.
        if obstacle_aware && squad.members.iter().all(|&id|
            metadata.current_path_clearance(id).is_some()
        ) {
            place_reinforcement_formation(
                squad, ships, &mut placements, metadata, metadata.bounds,
            )?;
        }
        if ordinal == 0 {
            let mut low = f32::INFINITY;
            let mut high = f32::NEG_INFINITY;
            for &ship_id in &squad.members {
                let ship = ships[&ship_id];
                let half_y = metadata.ship_catalog.sprite_size(ship.ship_type)
                    .ok_or("Reinforcement first squad has no sprite height")?.y * 0.5;
                let y = placements[&ship_id].y;
                low = low.min(y - half_y);
                high = high.max(y + half_y);
            }
            first_squad_center = Vec2::new(position.x, (low + high) * 0.5);
            if !first_squad_center.is_finite() {
                return Err("Reinforcement first squad center is invalid");
            }
        }
    }
    Ok(placements)
}

impl<A> NativeAuthoredLevel<NativePolicyOnlyMetadata, A> {
    pub(crate) fn stage_reinforcements(
        &mut self, plans: Vec<NativeReinforcementPlan>, dt: f32,
        vacant_squads: &HashMap<i64, i32>,
    ) -> Result<(), &'static str>
    {
        if plans.is_empty() { return Ok(()); }
        let mut squad_waves = HashMap::new();
        let mut seen_squads = HashSet::new();
        let mut waves = Vec::with_capacity(plans.len());
        let mut spawn_centers = Vec::with_capacity(plans.len());
        let mut planned_squad_order = Vec::with_capacity(plans.len());
        let mut previous_timed = 0.0;
        // Equal-time waves and simultaneous mission cues retain authored
        // construction order. Timed waves remain sorted relative to other
        // timed waves; cue-triggered waves may interleave them.
        for mut plan in plans {
            let due = match plan.activation {
                NativeReinforcementActivation::AfterSeconds(seconds) => {
                    let tick = (f64::from(seconds) / f64::from(dt)).floor() + 1.0;
                    if !seconds.is_finite() || seconds <= 0.0 ||
                        seconds < previous_timed ||
                        !tick.is_finite() || tick >= u64::MAX as f64 {
                        return Err("Timed reinforcements need positive, finite, nondecreasing times");
                    }
                    previous_timed = seconds;
                    ReinforcementDue::Tick(tick as u64)
                }
                NativeReinforcementActivation::OnCampaignCue(cue) => {
                    let event = NativeMissionEvent::CampaignCue(cue);
                    if !event.valid_for(self.campaign_dialogue,
                        |_| false, |_| false, |_| false) {
                        return Err("Reinforcement campaign cue is unavailable in this Level");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnShipDestroyed(ship_id) => {
                    let event = NativeMissionEvent::ShipDestroyed(ship_id);
                    if !event.valid_for(self.campaign_dialogue, |id|
                        self.entities.iter().any(|entity| matches!(
                            entity,
                            NativeAuthoredEntity::Ship(ship)
                                if ship.entity_id == id && !ship.vitals.is_dead()
                        )), |_| false, |_| false
                    ) {
                        return Err("Reinforcement ship-death trigger needs an authored source ship");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnShipDamaged(ship_id) => {
                    let event = NativeMissionEvent::ShipDamaged(ship_id);
                    if !event.valid_for(self.campaign_dialogue, |id|
                        self.entities.iter().any(|entity| matches!(entity,
                            NativeAuthoredEntity::Ship(ship)
                                if ship.entity_id == id && !ship.vitals.is_dead()
                        )), |_| false, |_| false
                    ) {
                        return Err("Reinforcement damage trigger requires a living authored source ship");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnShipExtracted(ship_id) |
                NativeReinforcementActivation::OnShipWarped(ship_id) => {
                    let event = if matches!(plan.activation,
                        NativeReinforcementActivation::OnShipExtracted(_))
                    {
                        NativeMissionEvent::ShipExtracted(ship_id)
                    } else { NativeMissionEvent::ShipWarped(ship_id) };
                    if !event.valid_for(self.campaign_dialogue, |id|
                        self.entities.iter().any(|entity| matches!(entity,
                            NativeAuthoredEntity::Ship(ship)
                                if ship.entity_id == id && !ship.vitals.is_dead()
                        )), |_| false, |_| false
                    ) {
                        return Err("Reinforcement extraction trigger requires a living authored source ship");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnMapObjectDestroyed(object_id) => {
                    let event = NativeMissionEvent::MapObjectDestroyed(object_id);
                    if !event.valid_for(self.campaign_dialogue,
                        |_| false,
                        |id| self.entities.iter().any(|entity| matches!(entity,
                            NativeAuthoredEntity::MapObject { object, body }
                                if object.entity_id == id &&
                                    object.health > 0 && body.active
                        )),
                        |_| false
                    ) {
                        return Err("Reinforcement MapObject trigger requires a living authored physical object");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnMiningAsteroidDepleted(asteroid_id) => {
                    let event = NativeMissionEvent::MiningAsteroidDepleted(asteroid_id);
                    if !event.valid_for(self.campaign_dialogue,
                        |_| false, |_| false,
                        |id| self.entities.iter().any(|entity| matches!(entity,
                            NativeAuthoredEntity::MiningAsteroid { obstacle, body }
                                if obstacle.entity_id == id && body.active
                        ))
                    ) {
                        return Err("Reinforcement mining trigger requires a live source ore collider");
                    }
                    ReinforcementDue::Event(event)
                }
            };
            // Uranus II/III's authored timers still announce the wave
            // when CurrentShips has no eligible SavedSquad of a requested
            // type. Never fabricate ships just to satisfy that dialogue.
            // Unrelated levels still require genuine physical squads.
            let empty_campaign_timer = matches!(self.campaign_dialogue,
                Some(CampaignDialogueSpec::UranusDefensive { .. } |
                     CampaignDialogueSpec::UranusNewThreat { .. } |
                     CampaignDialogueSpec::NeptuneProduction { .. } |
                     CampaignDialogueSpec::TitaniaMinesweeper { .. })
            ) && matches!(plan.activation,
                NativeReinforcementActivation::AfterSeconds(_));
            if (plan.squad_item_ids.is_empty() && !empty_campaign_timer) ||
                plan.spawn.is_some_and(|spawn| !spawn.is_finite()) ||
                plan.entry.is_some_and(|entry| !entry.is_finite())
            {
                return Err("Reinforcements need authored squads and finite spawn/entry coordinates");
            }
            // A source plan preserves AddReinforcementSquads order.
            // Independent SavedSquads each get a constructor call; only
            // the generated Carrier children share physical spacing.
            for &id in &plan.squad_item_ids {
                if !seen_squads.insert(id) {
                    return Err("A squad may arrive in only one reinforcement wave");
                }
            }
            // The original LevelConstructor skips an empty persistent
            // composition. A campaign casualty or reserve transfer can
            // empty a source squad (including its Carrier descendants).
            // Never create an empty wave or a phantom pending faction.
            // Unknown identities still fail the normal source validation.
            plan.squad_item_ids.retain(|id| !vacant_squads.contains_key(id));
            if plan.squad_item_ids.is_empty() && !empty_campaign_timer { continue; }
            for &id in &plan.squad_item_ids {
                squad_waves.insert(id, waves.len());
            }
            planned_squad_order.push(plan.squad_item_ids);
            spawn_centers.push(plan.spawn);
            // Unity LevelConstructor.PositionSquads skips Squad.Move when
            // moveToPoint == Vector2.zero. That is a source sentinel, not a
            // physical point order; use Unity's epsilon-aware equality.
            let entry = plan.entry.filter(|point| !point.unity_equal(Vec2::ZERO));
            waves.push(ReinforcementWave {
                due, entry, squads: Vec::new(), ships: Vec::new(),
                policy_agents: 0, group_agents: Vec::new(),
            });
        }
        if waves.is_empty() { return Ok(()); }
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
        // A policy reinforcement is a *dormant* authored agent, not a
        // controller enrolled at scene creation. Preflight every binding
        // and group participant before extracting any physical scene data.
        // Respect squad/member source order for reproducible registration.
        let mut deferred_group_ids = HashSet::new();
        for (wave_idx, ids) in planned_squad_order.iter().enumerate() {
            for squad_id in ids {
                for ship_id in &source_squads[squad_id].members {
                    let ship = source_ships[ship_id];
                    let group_team = self.groups.as_ref()
                        .and_then(|groups| groups.delivery.team_id(ship.side));
                    if let Some((side, group_id)) = self.metadata.deferred_policy_participant(
                        ship, group_team,
                    )? {
                        waves[wave_idx].policy_agents += 1;
                        if self.groups.is_some() {
                            let participant = (side, group_id.ok_or(
                                "MA-POCA policy reinforcement needs its source group identity",
                            )?);
                            if !deferred_group_ids.insert(participant) {
                                return Err("Reinforcement MA-POCA participant identities must be unique");
                            }
                            waves[wave_idx].group_agents.push((
                                ship.entity_id, participant.0, participant.1,
                            ));
                        }
                    }
                }
            }
        }
        if !deferred_group_ids.is_empty() {
            let groups = self.groups.as_ref()
                .ok_or("Policy reinforcements require existing authored MA-POCA groups")?;
            let actual: HashSet<_> = groups.participants.iter().copied().collect();
            if actual.len() != groups.participants.len() ||
                !deferred_group_ids.is_subset(&actual) {
                return Err("Policy reinforcement participant is absent from the authored group");
            }
        }
        // Source AddReinforcementSquads invokes the constructor once per
        // independent SavedSquad. Generated Carrier child squads must
        // follow their actual parent, belong entirely to that parent and
        // share its side, even when the spawn is inherited from a capture.
        for ids in &planned_squad_order {
            // An authentic depleted Uranus II timer may have no physical
            // squad to validate; it still publishes its scheduled cue.
            let Some(first) = ids.first() else { continue };
            let root_side = source_squads[first].side;
            let mut seen_carriers = HashSet::new();
            for id in ids {
                let squad = source_squads[id];
                if squad.side != root_side {
                    return Err("Reinforcement squads in one source deployment must share a side");
                }
                let mut child_parent = None;
                let mut child_count = 0;
                for ship_id in &squad.members {
                    let ship = source_ships[ship_id];
                    if let Some(parent) = reinforcement_carrier_parent(ship) {
                        if !seen_carriers.contains(&parent) ||
                            child_parent.is_some_and(|prior| prior != parent)
                        {
                            return Err("Reinforcement Carrier child squad lacks its preceding parent");
                        }
                        child_count += 1;
                        child_parent = Some(parent);
                    }
                }
                if child_count != 0 && child_count != squad.members.len() {
                    return Err("Reinforcement Carrier child squad mixes unrelated ships");
                }
                for ship_id in &squad.members {
                    if source_ships[ship_id].ship_type == 5 {
                        seen_carriers.insert(*ship_id);
                    }
                }
            }
        }
        // Resolve ALL wave positions before extracting the first deferred
        // ship or squad. The subsequent commit phase cannot fail on late
        // formation geometry or Carrier group membership.
        let mut planned_positions = HashMap::new();
        let mut offscreen_waves = vec![false; waves.len()];
        let bounds = self.broadphase_bounds;
        let has_obstacles = self.metadata.player_formation_options().has_obstacles;
        // One source Pathfinder raster serves BOTH constructor placement
        // passes for every authored wave; never rebuild it per squad.
        if has_obstacles && spawn_centers.iter().any(Option::is_some) {
            let bodies = crate::campaign_staging::authored_obstacle_bodies(&self.entities)?;
            self.metadata.navigation.refresh_static(
                bodies.iter().filter(|body| body.layer == 19)
                    .map(|body| (body.id, body)),
            ).map_err(|_| "Reinforcement static navigation map is invalid")?;
            self.metadata.navigation.refresh_dynamic(
                self.metadata.activate_collision_asteroids,
                bodies.iter().filter(|body|
                    body.source_asteroid_prefab_number.is_some()
                ).map(|body| (body.id, body)),
            ).map_err(|_| "Reinforcement asteroid navigation map is invalid")?;
        }
        for (wave_idx, ids) in planned_squad_order.iter().enumerate() {
            let Some(spawn) = spawn_centers[wave_idx] else { continue };
            let squads: Vec<&NativeAuthoredSquad> = ids.iter()
                .map(|id| source_squads[id]).collect();
            // Source ShouldStageOffscreenReinforcement: only a genuine
            // outside-to-inside route on an obstacle-bearing map skips
            // normal placement. A missing/zero entry is not such a route.
            let offscreen = has_obstacles &&
                (spawn.x < bounds.min.x || spawn.x > bounds.max.x ||
                 spawn.y < bounds.min.y || spawn.y > bounds.max.y) &&
                waves[wave_idx].entry.is_some_and(|entry|
                    !spawn.unity_equal(entry) &&
                    entry.x >= bounds.min.x && entry.x <= bounds.max.x &&
                    entry.y >= bounds.min.y && entry.y <= bounds.max.y);
            offscreen_waves[wave_idx] = offscreen;
            let positions = if offscreen {
                plan_offscreen_reinforcement(
                    &squads, &source_ships, spawn, &self.metadata,
                )?
            } else {
                plan_reinforcement_wave(
                    &squads, &source_ships, spawn, &self.metadata,
                )?
            };
            planned_positions.extend(positions);
        }
        // An explicit spawn on an obstacle-bearing map is valid when the
        // complete original two-pass squad formation fits at that exact
        // authored point. Preflight before extracting any deferred owner;
        // unsafe positions remain rejected instead of fabricating a
        // replacement Pathfinder destination or moving an existing ship.
        if !planned_positions.is_empty() &&
            self.metadata.player_formation_options().has_obstacles
        {
            // Match the source Pathfinder's ObstaclesLayerMask. Decorative
            // map borders and unrelated triggers do not block deployment.
            let obstacles = crate::campaign_staging::authored_obstacle_bodies(&self.entities)?
                .into_iter().filter(|body| body.layer == 19).collect::<Vec<_>>();
            for (&ship_id, &position) in &planned_positions {
                let ship = source_ships[&ship_id];
                let extents = self.metadata.ship_catalog.sprite_size(ship.ship_type)
                    .ok_or("Reinforcement spawn ship has no source sprite size")?;
                if !extents.is_finite() || extents.x <= 0.0 || extents.y <= 0.0 {
                    return Err("Reinforcement spawn ship has invalid physical size");
                }
                // The original source can start a wave outside the map
                // and order its real squads to enter through a playable
                // destination (Uranus II's lower flank starts at 600,-512).
                // Physics explicitly tracks out-of-bounds bodies. Do not
                // reject or teleport that authentic approach. A wave with
                // no in-map entry must still fit inside the playable area.
                let radius = extents.x.max(extents.y) * 0.5 + 2.0;
                let bounds = self.broadphase_bounds;
                let (wave_idx, _) = expected_ships[&ship_id];
                // Unity constructs an offscreen wave at the entry and then
                // applies SetOffscreenStartingPosition, deliberately skipping
                // Pathfinder placement at its outside starting center.
                // Its authored starting positions may overlap outside terrain.
                if offscreen_waves[wave_idx] { continue; }
                let enters_map = waves[wave_idx].entry.is_some_and(|entry|
                    entry.x >= bounds.min.x && entry.x <= bounds.max.x &&
                    entry.y >= bounds.min.y && entry.y <= bounds.max.y);
                let outside = position.x - radius < bounds.min.x ||
                    position.x + radius > bounds.max.x ||
                    position.y - radius < bounds.min.y ||
                    position.y + radius > bounds.max.y;
                // Source SetStartingPosition owns the final placement for
                // ships with authenticated clearance, including its explicit
                // requested-slot fallback when the map is fully obstructed.
                // Do not reject that source-authored overlap a second time.
                // Legacy captures lacking clearance retain geometric checks.
                let valid = self.metadata.current_path_clearance(ship_id).is_some() ||
                    (!(outside && !enters_map) &&
                        !obstacles.iter().any(|obstacle|
                            obstacle.navigation_closest_point(position).is_some_and(|point|
                                (point - position).length_squared() < radius * radius)));
                if !position.is_finite() || !valid {
                    return Err("Reinforcement authored spawn intersects the physical map or obstacle");
                }
            }
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
                self.reserved_ship_ids.push((ship.entity_id, ship.side));
                waves[wave].ships.push(ship);
            } else {
                initial_entities.push(NativeAuthoredEntity::Ship(ship));
            }
        }
        self.squads = initial_squads;
        self.entities = initial_entities;
        // Only physically present agents belong to the initial episode
        // group. The deferred participants are installed when their wave
        // spawns, before the next policy inference or group reward pass.
        if let Some(groups) = self.groups.as_mut() {
            groups.participants.retain(|participant|
                !deferred_group_ids.contains(participant));
        }
        // Membership, source links, entry eligibility and two-pass
        // placement have all been validated against the unmodified scene.
        // This phase only preserves canonical physical creation order.
        for wave in &mut waves {
            wave.ships.sort_unstable_by_key(|ship| ship.entity_id);
        }
        self.metadata.reinforcements = VecDeque::from(waves);
        self.metadata.reinforcement_wakeup =
            ReinforcementWakeup::from_waves(&self.metadata.reinforcements);
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
        if self.reinforcements.is_empty() {
            return Ok(());
        }
        if !self.reinforcement_wakeup.may_be_due(tick, world, events) {
            self.reinforcement_idle_ticks_skipped += 1;
            return Ok(());
        }
        self.reinforcement_wakeup_scans += 1;
        let mut navigation_refreshed = false;
        let mut changed = false;
        let mut index = 0;
        while index < self.reinforcements.len() {
            if !self.reinforcements[index].is_due(tick, world, events) {
                index += 1;
                continue;
            }
            // Source order is retained for waves released by the same
            // mission event. A later timed wave need not wait for an
            // unrelated earlier dialogue-triggered reinforcement.
            let wave = self.reinforcements.remove(index).expect("due wave");
            changed = true;
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
                        // Unity Squad.Move returns without issuing an order if
                        // Pathfinder cannot fit the formation at any reachable
                        // destination. This is not a failed reinforcement:
                        // the physical squad remains spawned at its authentic
                        // position and can receive later commands normally.
                        let _ = issue_squad_move(
                            world, &self.navigation, &members, entry, options,
                        ).map_err(|error| invalid(format!("{error:?}")))?;
                    }
                }
                squad.register(world, events.roster_mut()).map_err(|error| invalid(format!("{error:?}")))?;
            }
            // Registration happens strictly after live combat bodies and
            // roster membership exist. Next-tick decision synchronization
            // discovers their CombatWorld policy bindings normally.
            let policy_agents = wave.policy_agents;
            let group_agents = wave.group_agents.len();
            if !wave.group_agents.is_empty() {
                for (ship_id, side, group_id) in wave.group_agents {
                    let team = events.group_delivery().and_then(|group| group.team_id(side))
                        .ok_or_else(|| invalid("Missing MA-POCA team for policy reinforcements".into()))?;
                    let binding_revision = self.live_deferred_policy_binding(
                        world, ship_id, side, group_id, team,
                    ).map_err(|error| invalid(error.into()))?;
                    // Only the authentic live binding may register its
                    // source group Agent. The journal's admission clock
                    // and policy capture then share this revision.
                    events.group_delivery_mut().expect("validated source group")
                        .register_agent(side, group_id)
                        .map_err(|error| invalid(format!("Policy reinforcement group: {error:?}")))?;
                    events.record_policy_controller_admitted(
                        tick, ship_id, binding_revision, side, group_id,
                    ).map_err(|error| invalid(format!("Policy reinforcement admission: {error:?}")))?;
                }
            }
            events.record_reinforcements(
                tick, ships, squads, policy_agents, group_agents,
            )?;
        }
        if changed {
            self.reinforcement_wakeup = ReinforcementWakeup::from_waves(
                &self.reinforcements,
            );
        }
        Ok(())
    }
}
