//! One state machine for timed HiveMind mining commands.
use super::*;

pub(super) struct MiningRun {
    pub(super) target_id: i64,
    pub(super) destination_offset: Vec2,
    pub(super) formation: SquadFormationOptions,
    pub(super) members: Vec<SquadFormationMember>,
    pub(super) eligible: Vec<i64>,
    /// Enrollment bits parallel to eligible avoid O(squad size squared)
    /// repeated contains() checks on every physics step.
    pub(super) registered: Vec<bool>,
    /// Encounter order, not ID order: Unity assigns mining remainders to
    /// ShipsCurrentlyMining in the order FoundAsteroid registered them.
    pub(super) currently_mining: Vec<i64>,
    pub(super) mining_timer: Option<ScaledTimer>,
    pub(super) stop_moving_timer: Option<ScaledTimer>,
    pub(super) has_found_all: bool,
    pub(super) mined_tsv: i64,
}

impl MiningRun {
    /// Register initial overlap at Execute as well as subsequent
    /// collision entries, preserving the source command's encounter order.
    pub(super) fn update_contacts(&mut self, world: &CombatWorld) {
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
                        matches!(ship.ship_type, 4 | 9)
                    )
                );
                if self.currently_mining.is_empty() {
                    return Ok((Some(CommandFinishCause::NoMiningShipsRemain), 0));
                }
                let amount = world.mine_asteroid_for_ships(
                    self.target_id, &self.currently_mining,
                )?;
                self.mined_tsv = self.mined_tsv.checked_add(i64::from(amount))
                    .ok_or(GameCommandError::InvalidMiningAccounting)?;
                timer.finish_update(due);
                if world.mining_asteroid_position(self.target_id).is_none() {
                    return Ok((Some(CommandFinishCause::MiningAsteroidDestroyed), 0));
                }
            }
        }

        // Unity SetAndMove uses the squad's live members. A fallen
        // non-miner must not make an otherwise healthy mining command
        // fail its movement preflight on the next three-second poll.
        self.members.retain(|member| world.ship(member.ship_id)
            .is_some_and(|ship| !ship.vitals.is_dead()));
        if self.members.is_empty() {
            return Ok((Some(CommandFinishCause::NoMiningShipsRemain), 0));
        }
        let mut callbacks = 0;
        if let Some(timer) = command_poll.as_mut() {
            if let Some(due) = timer.begin_update(dt) {
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

