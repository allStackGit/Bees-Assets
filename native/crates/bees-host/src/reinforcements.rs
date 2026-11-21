//! Deferred deployment of authored squads and physical policy agents into a live Level.

use std::collections::{HashMap, HashSet, VecDeque};

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
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeReinforcementActivation {
    AfterSeconds(f32),
    OnCampaignCue(NativeMissionCue),
    OnShipDestroyed(i64),
}

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
            }
        }
        state
    }

    fn may_be_due(&self, tick: u64, world: &CombatWorld, events: &NativeLevelEventProcessor) -> bool {
        self.next_tick.is_some_and(|at| tick >= at) ||
            (self.campaign_event && events.campaign_cue_fired(tick, |_| true)) ||
            (self.ship_death_event && world.events().iter().any(|event|
                matches!(event, bees_sim::combat_world::CombatEvent::ShipDied {
                    end_kill: false, ..
                })
            ))
    }
}

impl ReinforcementWave {
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
                    if !event.valid_for(self.campaign_dialogue, |_| false) {
                        return Err("Reinforcement campaign cue is unavailable in this Level");
                    }
                    ReinforcementDue::Event(event)
                }
                NativeReinforcementActivation::OnShipDestroyed(ship_id) => {
                    let event = NativeMissionEvent::ShipDestroyed(ship_id);
                    if !event.valid_for(self.campaign_dialogue, |id|
                        self.entities.iter().any(|entity| matches!(
                            entity,
                            NativeAuthoredEntity::Ship(ship) if ship.entity_id == id
                        ))
                    ) {
                        return Err("Reinforcement ship-death trigger needs an authored source ship");
                    }
                    ReinforcementDue::Event(event)
                }
            };
            // Uranus II's authored timers still announce each wave when
            // CurrentShips has no eligible SavedSquad of a requested type.
            // Represent that real timer as an empty admission, never as
            // invented combat ships. All other gameplay requires a real
            // source squad before scheduling a reinforcement wave.
            let empty_uranus_timer = matches!(
                (self.campaign_dialogue, plan.activation),
                (Some(CampaignDialogueSpec::UranusDefensive { .. }),
                 NativeReinforcementActivation::AfterSeconds(_))
            );
            if (plan.squad_item_ids.is_empty() && !empty_uranus_timer) ||
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
            if plan.squad_item_ids.is_empty() && !empty_uranus_timer { continue; }
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
            let root_side = source_squads[&ids[0]].side;
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
        let bounds = self.broadphase_bounds;
        let has_obstacles = self.metadata.player_formation_options().has_obstacles;
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
            let obstacles = crate::campaign_staging::authored_obstacle_bodies(&self.entities)?;
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
                let enters_map = waves[wave_idx].entry.is_some_and(|entry|
                    entry.x >= bounds.min.x && entry.x <= bounds.max.x &&
                    entry.y >= bounds.min.y && entry.y <= bounds.max.y);
                let outside = position.x - radius < bounds.min.x ||
                    position.x + radius > bounds.max.x ||
                    position.y - radius < bounds.min.y ||
                    position.y + radius > bounds.max.y;
                if !position.is_finite() || (outside && !enters_map) ||
                    obstacles.iter().any(|obstacle|
                        obstacle.navigation_closest_point(position).is_some_and(|point|
                            (point - position).length_squared() < radius * radius))
                {
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
                        let plan = issue_squad_move(world, &self.navigation, &members, entry, options)
                            .map_err(|error| invalid(format!("{error:?}")))?;
                        if plan.is_none() { return Err(invalid(format!("Reinforcement squad {} has no reachable entry", squad.owner.squad_id))); }
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
