using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    /// <summary>
    /// Sends the ships to the selected asteroid to start mining 
    /// </summary>
    public class Mining : Command
    {
        public MiningAsteroid TargetAstroid;
        /// <summary>
        /// Ships in the squad that can mine asteroids
        /// </summary>
        public List<Ship> MiningShips = new List<Ship>();
        /// <summary>
        /// Ships in squad that are currently mining asteroids
        /// </summary>
        public List<Ship> ShipsCurrentlyMining = new List<Ship>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, MiningAsteroid asteroid)
        {
            if (asteroid != null) // Needs to be null check in case there were no asteroids
            {
                MiningShips = GetSquad().GetShips().Where((ship) => ship.IsMiningShip).ToList();
                TargetAstroid = asteroid;
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                PrepareDamageToSendEntries(1);

                // Check if any ships are already on the asteroid
                GetSquad().GetShips().ForEach((ship) =>
                {
                    if (ship.Collider.IsTouching(TargetAstroid.Collider))
                    {
                        if (!TargetAstroid.SquadsMining.Contains(ship.Squad))
                        {
                            TargetAstroid.SquadsMining.Add(ship.Squad);

                        }
                        FoundAsteroid(ship);
                    }
                });
                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);
                //InvokeRepeating(nameof(MoveToAsteroid), 0, CommandFrequency);
                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(600, Timeout); // 10 minutes
                    Level.AddTimer(TimeoutTimer);
                }

            }
            else
            {
                GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Mining);
                SetFinalize("The asteroid doesn't exist anymore, or there were no asteroids around");
            }

        }
        public override void ClearData()
        {
            base.ClearData();
            TargetAstroid = null;
            HasFoundAsteroid = false;
            MiningShips.Clear();
            ShipsCurrentlyMining.Clear();
        }

        private Vector2 _position;
        public void Timer()
        {
            if (!GetSquad().IsDead && !TargetAstroid.IsDead)
            {
                _position = TargetAstroid.GetPosition();
                SetAndMove(_position);
                GetSquad().Status = $"Moving to {TargetAstroid.Name} to start mining: {_position}";

            }
            else if (TargetAstroid.IsDead)
            {
                SetFinalize("Mining asteroid was destroyed");
            }
        }
        private ScaledTimer _miningTimer = new ScaledTimer();
        private ScaledTimer _stopMovingTowardsAsteroidTimer = new ScaledTimer();
        public bool HasFoundAsteroid = false;
        public void FoundAsteroid(Ship ship)
        {
            if (!HasFoundAsteroid)
            {
                ShipsCurrentlyMining.Add(ship);
                if (ship.HasShipAnimation)
                {
                    if (ship.ShipType == ConfigData.ShipTypes.Factory)
                    {
                        ship.ShipAnimationController.Activate();
                    }
                    else
                    {
                        ship.ShipAnimation.SetActive(true);
                    }
                }
                if (ShipsCurrentlyMining.Count == 1)
                {
                    _miningTimer.Reuse(5, Mine, true);
                    Level.AddTimer(_miningTimer);
                    //InvokeRepeating(nameof(Mine), 0, 3);
                }
                if (MiningShips.All((s) => s.IsDead || ShipsCurrentlyMining.Contains(s)))
                {
                    HasFoundAsteroid = true;
                    _stopMovingTowardsAsteroidTimer.Reuse(5, StopMovingTowardsAsteroid);
                    Level.AddTimer(_stopMovingTowardsAsteroidTimer);
                    //Invoke(nameof(StopMovingTowardsAsteroid), 5);
                }
            }

        }

        public void StopMovingTowardsAsteroid()
        {
            Level.CancelTimer(CommandTimer);
            //CancelInvoke(nameof(MoveToAsteroid));
        }
        private int _miningRate, _amountMined, _amountPerShip;
        public void Mine() // [stats-method]
        {
            if (!TargetAstroid.IsDead)
            {
                ShipsCurrentlyMining = ShipsCurrentlyMining.Where((s) => !s.IsDead).ToList();
                if (ShipsCurrentlyMining.Count > 0)
                {
                    //Debug.Log($"There are {ShipsCurrentlyMining.Count} ships mining for {GetSquad().Name}");
                    _miningRate = ConfigData.MiningRate * ShipsCurrentlyMining.Count;
                    _amountMined = math.min(_miningRate, TargetAstroid.Health); // [TSV] The health of the mining asteroids should be adjusted if the TSV is adjusted

                    Tsv += _amountMined;

                    TargetAstroid.Health -= _amountMined;

                    if (GetSquad().IsUserControlled && ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
                    {
                        Level.State.PlayerMineralsMined += _amountMined;
                        Stage.Menus.UpdateMineralsMined(Level.State.PlayerMineralsMined, Level.MaxMinerals);
                    }
                    //Debug.Log($"{GetSquad().Name} mined {_amountMined} from {TargetAstroid.Name}. It has {TargetAstroid.Health} health left");

                    _amountPerShip = _amountMined / ShipsCurrentlyMining.Count; // this isn't exactly the same as MiningRate because the ships might have mined the last of the asteroid
                    ShipsCurrentlyMining.ForEach((ship) =>
                    {
                        if (!ship.IsDead)
                        {
                            ship.FleetShip.MineralsMinedThisLevel += _amountPerShip;
                            ship.Tsv = Utilities.CalculateTsv(ship);
                            //Debug.Log($"Just added {_amountPerShip} to {ship.Name}:{ship.FleetShip.Id} TSV. It's now at {ship.Tsv}");
                        }

                    });

                    if (TargetAstroid.Health == 0)
                    {
                        TargetAstroid.Kill(false);
                    }
                }
                else
                {
                    SetFinalize("Asteroid has died");
                }
            }
            

        }
        public void CleanupAsteroid()
        {
            if (TargetAstroid != null && !TargetAstroid.IsDead)
            {
                TargetAstroid.SquadsMining.Remove(GetSquad());
            }
        }
        public override void SetFinalize(string cause)
        {
            Level.CancelTimer(_miningTimer);
            Level.CancelTimer(_stopMovingTowardsAsteroidTimer);
            CleanupAsteroid();
            //Debug.Log($"Finalizing mining command for {Squad}");
            GetSquad().GetShips().ForEach((ship) =>
            {
                if (ship.HasShipAnimation)
                {
                    if (ship.ShipType == ConfigData.ShipTypes.Factory)
                    {
                        ship.ShipAnimationController.Deactivate();
                    }
                    else
                    {
                        ship.ShipAnimation.SetActive(false);
                    }
                }
            });
            base.SetFinalize(cause);
        }
    }
}