//! Deferred deployment of authored, non-policy squads into a live Level.

use std::collections::{HashMap, VecDeque};

use bees_gameplay::level_events::NativeLevelEventProcessor;
use bees_gameplay::level_host::NativeLevelActivationContext;
use bees_gameplay::level_rules::NativeLevelStepError;
use bees_gameplay::player_inputs::NativeLevelPlayerContext;
use bees_sim::{combat_world::CombatWorld, geometry::Vec2, squad_movement::{
    SquadFormationMember, issue_squad_move,
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

impl<A> NativeAuthoredLevel<NativePolicyOnlyMetadata, A> {
    pub(crate) fn stage_reinforcements(&mut self, plans: Vec<NativeReinforcementPlan>, dt: f32)
        -> Result<(), &'static str>
    {
        if plans.is_empty() { return Ok(()); }
        let mut squad_waves = HashMap::new();
        let mut waves = Vec::with_capacity(plans.len());
        let mut spawn_centers = Vec::with_capacity(plans.len());
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
            for id in plan.squad_item_ids {
                if squad_waves.insert(id, waves.len()).is_some() {
                    return Err("A squad may arrive in only one reinforcement wave");
                }
            }
            previous = plan.after_seconds;
            spawn_centers.push(plan.spawn);
            waves.push(ReinforcementWave {
                tick: tick as u64, entry: plan.entry,
                squads: Vec::new(), ships: Vec::new(),
            });
        }
        let mut ship_waves = HashMap::new();
        let mut initial_squads = Vec::new();
        for squad in std::mem::take(&mut self.squads) {
            if let Some(wave) = squad_waves.remove(&squad.owner.squad_id) {
                ship_waves.extend(squad.members.iter().map(|&id| (id, wave)));
                waves[wave].squads.push(squad);
            } else { initial_squads.push(squad); }
        }
        if !squad_waves.is_empty() { return Err("Reinforcements reference an unknown source squad item ID"); }
        let mut initial_entities = Vec::new();
        for entity in std::mem::take(&mut self.entities) {
            let NativeAuthoredEntity::Ship(ship) = entity else { initial_entities.push(entity); continue };
            let wave = ship_waves.get(&ship.entity_id).copied();
            let striker_carrier = match &ship.special {
                Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) => carrier.carrier_id,
                _ => None,
            };
            if [ship.drone_carrier_id, striker_carrier].into_iter().flatten().any(|id|
                ship_waves.get(&id).copied() != wave)
            {
                return Err("Reinforcement carrier and child must deploy together");
            }
            if let Some(wave) = wave {
                if ship.policy.is_some() || ship.motion.is_policy_controlled {
                    return Err("Reinforcements cannot reserve a live policy controller");
                }
                self.reserved_ship_ids.push(ship.entity_id);
                waves[wave].ships.push(ship);
            } else { initial_entities.push(NativeAuthoredEntity::Ship(ship)); }
        }
        self.squads = initial_squads;
        self.entities = initial_entities;
        // Deferred squads bypass the ordinary initial roster registration.
        // Verify their physical membership here; a missing imported hull or
        // repeated ship ID must not fail only after a long training run.
        for wave in &waves {
            let expected = wave.squads.iter().try_fold(0usize, |total, squad| {
                if squad.members.is_empty() { return None; }
                total.checked_add(squad.members.len())
            }).ok_or("Reinforcement squads need nonempty, bounded membership")?;
            if wave.ships.len() != expected {
                return Err("Reinforcement squads need one authored physical ship per member");
            }
        }
        // Preflight and apply physical spawn positions during setup, not
        // hundreds of seconds into a battle. The source Unity LevelConstructor
        // calls Squad.SetOffsets after physical placement: a LIVE scene
        // snapshot already stores the adjusted OffsetFromCenter. Multiplying
        // that offset again would spread wide and Queen hulls twice.
        for (wave, spawn) in waves.iter_mut().zip(spawn_centers) {
            if let Some(spawn) = spawn {
                for ship in &mut wave.ships {
                    let squad = wave.squads.iter().find(|squad|
                        squad.members.contains(&ship.entity_id))
                        .ok_or("Reinforcement ship has no staged squad")?;
                    let offset = self.metadata.stored_formation_offset(ship.entity_id)
                        .ok_or("Reinforcement ship has no source formation offset")?;
                    let position = spawn + if squad.members.len() == 1 {
                        Vec2::ZERO
                    } else {
                        offset
                    };
                    if !position.is_finite() {
                        return Err("Reinforcement spawn and formation overflowed world coordinates");
                    }
                    ship.motion.position = position;
                }
            }
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
