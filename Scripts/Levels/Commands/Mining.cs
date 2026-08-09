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
                MiningShips = GetSquad().GetShips().Where((ship) => ship.IsMiningShip && !ship.IsDead).ToList();
                TargetAstroid = asteroid;
                if (MiningShips.Count == 0)
                {
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Mining);
                    SetFinalize("This squad has no live mining ships");
                    return;
                }

                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                PrepareDamageToSendEntries(1);

                // Check if any mining ships are already on the asteroid.
                MiningShips.ToList().ForEach((ship) =>
                {
                    if (!ship.IsDead && ship.Collider.IsTouching(TargetAstroid.Collider))
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
            if (HasFoundAsteroid || ship == null || ship.IsDead || !ship.IsMiningShip || !MiningShips.Contains(ship) || ShipsCurrentlyMining.Contains(ship))
            {
                return;
            }

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
            }
            if (MiningShips.All((s) => s.IsDead || ShipsCurrentlyMining.Contains(s)))
            {
                HasFoundAsteroid = true;
                _stopMovingTowardsAsteroidTimer.Reuse(5, StopMovingTowardsAsteroid);
                Level.AddTimer(_stopMovingTowardsAsteroidTimer);
            }
        }

        public void StopMovingTowardsAsteroid()
        {
            Level.CancelTimer(CommandTimer);
        }
        private int _miningRate, _amountMined, _amountPerShip;
        public void Mine() // [stats-method]
        {
            if (!TargetAstroid.IsDead)
            {
                ShipsCurrentlyMining = ShipsCurrentlyMining.Where((s) => s != null && !s.IsDead && s.IsMiningShip).ToList();
                if (ShipsCurrentlyMining.Count > 0)
                {
                    _miningRate = ConfigData.MiningRate * ShipsCurrentlyMining.Count;
                    _amountMined = math.min(_miningRate, TargetAstroid.Health);

                    Tsv += _amountMined;
                    TargetAstroid.Health -= _amountMined;

                    if (GetSquad().IsUserControlled && ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
                    {
                        Level.State.PlayerMineralsMined += _amountMined;
                        Stage.Menus.UpdateMineralsMined(Level.State.PlayerMineralsMined, Level.MaxMinerals);
                    }

                    _amountPerShip = _amountMined / ShipsCurrentlyMining.Count;
                    ShipsCurrentlyMining.ForEach((ship) =>
                    {
                        ship.FleetShip.MineralsMinedThisLevel += _amountPerShip;
                        ship.Tsv = Utilities.CalculateTsv(ship);
                    });

                    if (TargetAstroid.Health == 0)
                    {
                        TargetAstroid.Kill(false);
                    }
                }
                else
                {
                    SetFinalize("No mining ships remain at the asteroid");
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