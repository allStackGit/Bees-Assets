//! One state machine for timed HiveMind mining commands.
use super::*;
use bees_sim::combat_world::is_mining_ship_type;

pub(super) struct MiningRun {
    target_id: i64,
    destination_offset: Vec2,
    formation: SquadFormationOptions,
    pub(super) members: Vec<SquadFormationMember>,
    eligible: Vec<i64>,
    /// Enrollment bits parallel to eligible avoid O(squad size squared)
    /// repeated contains() checks on every physics step.
    registered: Vec<bool>,
    /// Encounter order, not ID order: Unity assigns mining remainders to
    /// ShipsCurrentlyMining in the order FoundAsteroid registered them.
    currently_mining: Vec<i64>,
    mining_timer: Option<ScaledTimer>,
    stop_moving_timer: Option<ScaledTimer>,
    has_found_all: bool,
    pub(super) mined_tsv: i64,
}

impl MiningRun {
    pub(super) fn start(
        world: &CombatWorld, random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
    ) -> Result<Option<Self>, GameCommandError> {
        // Unity Squad.GetNearestMiningAsteroid selects by distance
        // from the current live squad bounds center. An absent
        // target or no mining ships bans Mining for this strategy
        // response rather than installing a nonfunctional command.
        let Some((target_id, _)) =
            world.nearest_mining_asteroid(squad.squad_center)
        else { return Ok(None); };
        let spread = world.mining_asteroid_spread_radius(target_id)
            .ok_or(GameCommandError::InvalidMiningTarget)?;
        let mut eligible = Vec::new();
        for member in squad.members {
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if !ship.vitals.is_dead() &&
                is_mining_ship_type(ship.ship_type) {
                if world.ship_fleet_identity(member.ship_id).is_none() {
                    return Err(GameCommandError::InvalidSquad);
                }
                eligible.push(member.ship_id);
            }
        }
        if eligible.is_empty() {
            return Ok(None);
        }
        // Unity Random.insideUnitCircle uses a uniform disk. Use
        // world-local seeded RNG for comparable statistics, not
        // a per-process global random stream.
        let angle = next_unit(random) * std::f32::consts::TAU;
        let radius = next_unit(random).sqrt() * spread;
        let mut run = Self {
            target_id,
            destination_offset: Vec2::new(
                angle.cos() * radius, angle.sin() * radius,
            ),
            formation: squad.formation,
            members: squad.members.to_vec(),
            registered: vec![false; eligible.len()],
            eligible,
            currently_mining: Vec::new(),
            mining_timer: None,
            stop_moving_timer: None,
            has_found_all: false,
            mined_tsv: 0,
        };
        run.update_contacts(world);
        Ok(Some(run))
    }

    /// Register initial overlap at Execute as well as subsequent
    /// collision entries, preserving the source command's encounter order.
    fn update_contacts(&mut self, world: &CombatWorld) {
        // Contact is checked on the actual cached physical hull,
        // not an extra "close enough" radius or guessed collision grid.
        if !self.has_found_all {
            for (slot, &id) in self.eligible.iter().enumerate() {
                if self.registered[slot] {
                    continue;
                }
                if world.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) &&
                    world.ship_touches_mining_asteroid(id, self.target_id) {
                    self.registered[slot] = true;
                    self.currently_mining.push(id);
                    if self.mining_timer.is_none() {
                        // Source FoundAsteroid starts the five-second
                        // recurring Mine timer on first actual contact.
                        self.mining_timer = Some(ScaledTimer::new(
                            MINING_RATE_SECONDS, true, true,
                        ));
                    }
                }
            }
            // Dead ships no longer block the source "all miners reached"
            // transition. It cancels movement polling after five seconds,
            // not the ongoing mining timer.
            if self.mining_timer.is_some() &&
                self.eligible.iter().enumerate().all(|(i, id)|
                !world.ship(*id).is_some_and(|ship| !ship.vitals.is_dead()) ||
                self.registered[i]
            ) {
                self.has_found_all = true;
                self.stop_moving_timer = Some(ScaledTimer::new(
                    MINING_RATE_SECONDS, false, false,
                ));
            }
        }

    }

    pub(super) fn advance(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        command_poll: &mut Option<ScaledTimer>,
        dt: f32,
    ) -> Result<(Option<CommandFinishCause>, u64), GameCommandError> {
        if world.mining_asteroid_position(self.target_id).is_none() {
            return Ok((Some(CommandFinishCause::MiningAsteroidDestroyed), 0));
        }

        self.update_contacts(world);

        if let Some(timer) = self.mining_timer.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                // Source Mine drops dead miners but keeps miners that
                // drift outside the asteroid after first touch.
                self.currently_mining.retain(|id|
                    world.ship(*id).is_some_and(|ship|
                        !ship.vitals.is_dead() &&
                        is_mining_ship_type(ship.ship_type)
                    )
                );
                if self.currently_mining.is_empty() {
                    return Ok((Some(CommandFinishCause::NoMiningShipsRemain), 0));
                }
                let amount = world.mine_asteroid_for_ships(
                    self.target_id, &self.currently_mining,
                )?;
                self.mined_tsv = self.mined_tsv.checked_add(i64::from(amount))
                    .ok_or(GameCommandError::InvalidCapabilityAccounting)?;
                timer.finish_update(due);
                if world.mining_asteroid_position(self.target_id).is_none() {
                    return Ok((Some(CommandFinishCause::MiningAsteroidDestroyed), 0));
                }
            }
        }

        // Unity SetAndMove uses the squad's live members. A fallen
        // non-miner must not make an otherwise healthy mining command
        // fail its movement preflight on the next three-second poll.
        let mut callbacks = 0;
        if let Some(timer) = command_poll.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                // Live membership only matters on a movement callback,
                // not every fixed physics tick of a multi-minute command.
                self.members.retain(|member| world.ship(member.ship_id)
                    .is_some_and(|ship| !ship.vitals.is_dead()));
                if self.members.is_empty() {
                    return Ok((Some(CommandFinishCause::NoMiningShipsRemain), 0));
                }
                let position = world.mining_asteroid_position(self.target_id)
                    .ok_or(GameCommandError::InvalidMiningTarget)?;
                issue_squad_move(
                    world, navigation, &self.members,
                    position + self.destination_offset, self.formation,
                )?;
                timer.finish_update(due);
                callbacks += 1;
            }
        }
        if let Some(timer) = self.stop_moving_timer.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
                timer.finish_update(due);
                *command_poll = None;
                self.stop_moving_timer = None;
            }
        }
        Ok((None, callbacks))
    }
}

