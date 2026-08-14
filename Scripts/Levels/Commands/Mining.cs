using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    /// <summary>
    /// Sends the ships to the selected asteroid to start mining.
    /// </summary>
    public class Mining : Command
    {
        public MiningAsteroid TargetAstroid;
        public List<Ship> MiningShips = new List<Ship>();
        public List<Ship> ShipsCurrentlyMining = new List<Ship>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, MiningAsteroid asteroid)
        {
            if (asteroid == null)
            {
                GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Mining);
                SetFinalize("The asteroid doesn't exist anymore, or there were no asteroids around");
                return;
            }

            MiningShips.Clear();
            List<Ship> squadShips = GetSquad().GetShips();
            for (int i = 0; i < squadShips.Count; i++)
            {
                Ship ship = squadShips[i];
                if (ship.IsMiningShip && !ship.IsDead)
                {
                    MiningShips.Add(ship);
                }
            }

            TargetAstroid = asteroid;
            if (MiningShips.Count == 0)
            {
                GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Mining);
                SetFinalize("This squad has no live mining ships");
                return;
            }

            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
            PrepareDamageToSendEntries(1);

            for (int i = 0; i < MiningShips.Count; i++)
            {
                Ship ship = MiningShips[i];
                if (!ship.IsDead && ship.Collider.IsTouching(TargetAstroid.Collider))
                {
                    if (!TargetAstroid.SquadsMining.Contains(ship.Squad))
                    {
                        TargetAstroid.SquadsMining.Add(ship.Squad);
                    }
                    FoundAsteroid(ship);
                }
            }

            if (!HasFoundAsteroid)
            {
                _position = TargetAstroid.GetPosition();
                SetAndMove(_position);
                if (!Stage.IsTraining)
                {
                    GetSquad().Status = $"Moving to {TargetAstroid.Name} to start mining: {_position}";
                }
            }

            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
            Level.AddTimer(CommandTimer);
            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(600, Timeout);
                Level.AddTimer(TimeoutTimer);
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
            if (GetSquad().IsDead)
            {
                return;
            }
            if (TargetAstroid.IsDead)
            {
                SetFinalize("Mining asteroid was destroyed");
                return;
            }

            if (!HasFoundAsteroid && !Stage.IsTraining)
            {
                GetSquad().Status = $"Moving to {TargetAstroid.Name} to start mining: {TargetAstroid.GetPosition()}";
            }
        }

        private readonly ScaledTimer _miningTimer = new ScaledTimer();
        private readonly ScaledTimer _stopMovingTowardsAsteroidTimer = new ScaledTimer();
        public bool HasFoundAsteroid;

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

            bool allMiningShipsReady = true;
            for (int i = 0; i < MiningShips.Count; i++)
            {
                Ship miningShip = MiningShips[i];
                if (!miningShip.IsDead && !ShipsCurrentlyMining.Contains(miningShip))
                {
                    allMiningShipsReady = false;
                    break;
                }
            }
            if (allMiningShipsReady)
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

        private int _miningRate, _amountMined, _baseAmountPerShip, _miningRemainder;
        public void Mine()
        {
            if (TargetAstroid.IsDead)
            {
                return;
            }

            for (int i = ShipsCurrentlyMining.Count - 1; i >= 0; i--)
            {
                Ship ship = ShipsCurrentlyMining[i];
                if (ship == null || ship.IsDead || !ship.IsMiningShip)
                {
                    ShipsCurrentlyMining.RemoveAt(i);
                }
            }
            if (ShipsCurrentlyMining.Count == 0)
            {
                SetFinalize("No mining ships remain at the asteroid");
                return;
            }

            _miningRate = ConfigData.MiningRate * ShipsCurrentlyMining.Count;
            _amountMined = math.min(_miningRate, TargetAstroid.Health);
            Tsv += _amountMined;
            TargetAstroid.Health -= _amountMined;

            if (GetSquad().IsUserControlled && ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Level.State.PlayerMineralsMined += _amountMined;
                Stage.Menus.UpdateMineralsMined(Level.State.PlayerMineralsMined, Level.MaxMinerals);
            }

            _baseAmountPerShip = _amountMined / ShipsCurrentlyMining.Count;
            _miningRemainder = _amountMined % ShipsCurrentlyMining.Count;
            for (int i = 0; i < ShipsCurrentlyMining.Count; i++)
            {
                Ship ship = ShipsCurrentlyMining[i];
                ship.FleetShip.MineralsMinedThisLevel += _baseAmountPerShip + (i < _miningRemainder ? 1 : 0);
                ship.Tsv = Utilities.CalculateTsv(ship);
            }

            if (TargetAstroid.Health == 0)
            {
                TargetAstroid.Kill(false);
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
            List<Ship> squadShips = GetSquad().GetShips();
            for (int i = 0; i < squadShips.Count; i++)
            {
                Ship ship = squadShips[i];
                if (!ship.HasShipAnimation)
                {
                    continue;
                }
                if (ship.ShipType == ConfigData.ShipTypes.Factory)
                {
                    ship.ShipAnimationController.Deactivate();
                }
                else
                {
                    ship.ShipAnimation.SetActive(false);
                }
            }
            base.SetFinalize(cause);
        }
    }
}