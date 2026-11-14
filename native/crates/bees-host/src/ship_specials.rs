//! Immutable source ship-special inputs shared by scene startup and the
//! Level's spawned-ship metadata. All execution remains in CombatWorld.
use std::sync::Arc;
use bees_sim::combat_world::{CombatWorld, CombatWorldError, GameStrikerCarrier, ScoutBeaconSpec, QueenSpawnSpec};
use bees_sim::combat_perception_bridge::AuthoredShipPerception;
use bees_sim::perception_weapons::SelfWeapon;
use crate::policy_only_metadata::NativeSourceVision;

#[derive(Clone, Debug)]
pub struct NativeBeaconTemplate {
    pub physical: ScoutBeaconSpec,
    pub perception: AuthoredShipPerception,
    pub vision: NativeSourceVision,
}

#[derive(Clone, Debug)]
pub struct NativeQueenTemplate {
    pub physical: QueenSpawnSpec,
    pub perception: AuthoredShipPerception,
    pub vision: NativeSourceVision,
    /// Exact source Bomb observation supplied only by a policy-enabled
    /// native scene. Gameplay uses the physical Bomb contacts directly.
    pub policy_weapon: Option<SelfWeapon>,
}

#[derive(Clone, Debug)]
pub enum NativeAuthoredShipSpecial {
    Barge { power: i32, can_override_bounds: Option<bool> },
    FireBarge { power: i32 },
    Striker { power: i32, carrier: GameStrikerCarrier },
    YellowJacket { power: i32 },
    Scout { beacon: Arc<NativeBeaconTemplate>, enabled: bool },
    Queen { minions: Arc<NativeQueenTemplate> },
}

impl NativeAuthoredShipSpecial {
    pub(crate) fn configure(self, world: &mut CombatWorld, id: i64, policy: bool)
        -> Result<(), CombatWorldError>
    {
        match self {
            Self::FireBarge { power } => {
                world.configure_fire_barge_bomb(id, power)?;
                if policy { world.configure_policy_fire_barge(id, power)?; }
            }
            Self::Striker { power, carrier } => {
                if policy { world.configure_policy_striker(id, carrier.carrier_id, power)?; }
                else { world.configure_game_striker(id, carrier)?; }
            }
            Self::Barge { power, can_override_bounds } => {
                if policy { world.configure_policy_barge(id, power)?; }
                else if let Some(can_override_bounds) = can_override_bounds {
                    world.configure_game_barge(id, power, can_override_bounds)?;
                }
                // Legacy snapshots never authenticated this source flag:
                // retain automated Charge support but do not fabricate
                // the player-specific bounds capability.
            }
            Self::YellowJacket { power } if policy => world.configure_policy_yellow_jacket(id, power)?,
            Self::Queen { minions } => {
                world.configure_queen_spawning(id, Arc::new(minions.physical.clone()))?;
            }
            Self::Scout { beacon, enabled } => {
                if policy {
                    world.configure_policy_scout(id, beacon.physical.clone())?;
                    world.set_policy_scout_enabled(id, enabled)?;
                } else {
                    world.configure_game_scout(id, beacon.physical.clone())?;
                    world.set_game_scout_enabled(id, enabled)?;
                }
            }
            // Non-policy YellowJacket attacks remain command-owned and
            // receive their live weapon power through the command context.
            Self::YellowJacket { .. } => {},
        }
        Ok(())
    }
}
