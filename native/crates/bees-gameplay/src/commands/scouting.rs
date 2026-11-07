//! Source HiveMind Scouting: five-second movement, ten-second
//! game-Scout beacon drops, and five-second finish after verified sighting.
//! Uses one CombatWorld; discovery/attribution remain with the Level host.

use super::*;
use bees_sim::combat_world::SCOUT_TYPE;

pub(super) const SCOUT_MOVE_SECONDS: f32 = 5.0;
const BEACON_POLL_SECONDS: f32 = 10.0;
const FINISH_AFTER_SIGHTING_SECONDS: f32 = 5.0;
const RANDOM_STEP_MIN_DISTANCE: f32 = 10.0;

pub(super) struct ScoutingRun {
    side: i32,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    max_random_distance: f32,
    scout_ids: Vec<i64>,
    beacon_timer: Option<ScaledTimer>,
    after_sighting: Option<ScaledTimer>,
}

impl ScoutingRun {
    pub(super) fn start(
        world: &mut CombatWorld, navigation: &NavigationRuntime,
        random: &mut RandomStream, squad: NativeSquadOrder<'_>,
        settings: HiveMindCommandSettings,
    ) -> Result<Self, GameCommandError> {
        let context = squad.scouting
            .ok_or(GameCommandError::MissingScoutingContext)?;
        if !context.valid() {
            return Err(GameCommandError::MissingScoutingContext);
        }
        let mut side = None;
        let mut scout_ids = Vec::new();
        for &member in squad.members {
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if ship.vitals.is_dead() {
                return Err(GameCommandError::InvalidSquad);
            }
            if side.is_some_and(|old| old != ship.side) {
                return Err(GameCommandError::InvalidSquad);
            }
            side = Some(ship.side);
            if ship.ship_type == SCOUT_TYPE &&
                ship.side == context.human_side {
                if !world.game_scout_configured(member.ship_id) {
                    return Err(GameCommandError::MissingGameScoutConfiguration(
                        member.ship_id,
                    ));
                }
                scout_ids.push(member.ship_id);
            }
        }
        let side = side.ok_or(GameCommandError::InvalidSquad)?;
        // Unity initial RandomCoordinate uses Squad.MaxSight as the
        // per-axis minimum displacement, not a fixed average or radius.
        let destination = choose_random_coordinate(
            random, squad.squad_center, squad.formation.map_bounds,
            settings.ai_random_max_distance, context.max_sight,
        );
        issue_squad_move(
            world, navigation, squad.members, destination, squad.formation,
        )?;
        let beacon_timer = if scout_ids.is_empty() { None } else {
            Some(ScaledTimer::new(BEACON_POLL_SECONDS, true, false))
        };
        Ok(Self {
            side, members: squad.members.to_vec(),
            formation: squad.formation,
            max_random_distance: settings.ai_random_max_distance,
            scout_ids, beacon_timer, after_sighting: None,
        })
    }

    /// Only invoke for Unity HivemindVision.RecordShipSighting's first
    /// side-wide enemy sighting, credited to the real reward Squad. A
    /// Beacon minion sighting must reference its MotherSquad lifetime.
    pub(super) fn found_first_ships(&mut self, side: i32) -> bool {
        if side != self.side || self.after_sighting.is_some() {
            return false;
        }
        self.after_sighting = Some(ScaledTimer::new(
            FINISH_AFTER_SIGHTING_SECONDS, false, false,
        ));
        true
    }

    pub(super) fn advance(
        &mut self, world: &mut CombatWorld,
        navigation: &NavigationRuntime, random: &mut RandomStream,
        command_poll: &mut Option<ScaledTimer>, dt: f32,
        owner: SquadLifetime,
        mut source_center: impl FnMut(
            SquadLifetime, &CombatWorld,
        ) -> Result<Vec2, GameCommandError>,
    ) -> Result<(Option<CommandFinishCause>, u64), GameCommandError> {
        let mut callbacks = 0;
        if let Some(due) = command_poll.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            callbacks += 1;
            // The source checks live GetShips() on each movement poll.
            self.members.retain(|member| world.ship(member.ship_id)
                .is_some_and(|ship| !ship.vitals.is_dead()));
            if self.members.is_empty() {
                return Ok((Some(CommandFinishCause::SquadRetired), callbacks));
            }
            let all_reached = self.members.iter().all(|member|
                world.ship_body(member.ship_id).is_some_and(|body|
                    body.active && !body.motion.has_target_coordinates
                )
            );
            if all_reached {
                // Unity Squad.GetPosition uses authored *sprite* extents,
                // not collision-hull bounds. Only ask the Level for this
                // geometry when the five-second timer must choose a new
                // destination; no additional per-step snapshot is built.
                let center = source_center(owner, world)?;
                if !center.is_finite() {
                    return Err(GameCommandError::MissingScoutingContext);
                }
                let destination = choose_random_coordinate(
                    random, center, self.formation.map_bounds,
                    self.max_random_distance, RANDOM_STEP_MIN_DISTANCE,
                );
                issue_squad_move(world, navigation, &self.members,
                    destination, self.formation)?;
            }
            command_poll.as_mut().expect("active command timer")
                .finish_update(due);
        }
        if let Some(due) = self.beacon_timer.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            callbacks += 1;
            // Unity Scouting.DropScoutBeacons visits its initial
            // GetShips-ordered Scout list from last to first, removing dead
            // Scouts as it goes. Reversing matters if the side's available
            // Beacon slots are limited: a different Scout would spawn.
            self.scout_ids.retain(|&id| world.ship(id)
                .is_some_and(|ship| !ship.vitals.is_dead()));
            for &id in self.scout_ids.iter().rev() {
                // Each Scout has its own world-owned charges and cooldown.
                world.try_drop_game_scout_beacon(id)?;
            }
            if self.scout_ids.is_empty() {
                // The source keeps calling an empty timer. No new Scouts
                // are added during this command, so omit that idle work.
                self.beacon_timer = None;
            } else {
                self.beacon_timer.as_mut().expect("active beacon timer")
                    .finish_update(due);
            }
        }
        if let Some(due) = self.after_sighting.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            self.after_sighting.as_mut()
                .expect("active sighting timer").finish_update(due);
            return Ok((Some(CommandFinishCause::ScoutingFoundShips), callbacks));
        }
        Ok((None, callbacks))
    }
}
