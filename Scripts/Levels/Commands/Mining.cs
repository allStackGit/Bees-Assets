using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using System.Linq;
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

            MiningShips = GetSquad().GetShips().Where(ship => ship.IsMiningShip && !ship.IsDead).ToList();
            TargetAstroid = asteroid;
            if (MiningShips.Count == 0)
            {
                GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Mining);
                SetFinalize("This squad has no live mining ships");
                return;
            }

            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
            PrepareDamageToSendEntries(1);

            MiningShips.ToList().ForEach(ship =>
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
            if (MiningShips.All(shipInSquad => shipInSquad.IsDead || ShipsCurrentlyMining.Contains(shipInSquad)))
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

            ShipsCurrentlyMining = ShipsCurrentlyMining
                .Where(ship => ship != null && !ship.IsDead && ship.IsMiningShip)
                .ToList();
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

            // Preserve the complete mined amount in persistent FleetShip accounting. Integer
            // division alone loses the remainder and makes fleet totals disagree with the
            // asteroid/command/player counters.
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
            GetSquad().GetShips().ForEach(ship =>
            {
                if (!ship.HasShipAnimation)
                {
                    return;
                }
                if (ship.ShipType == ConfigData.ShipTypes.Factory)
                {
                    ship.ShipAnimationController.Deactivate();
                }
                else
                {
                    ship.ShipAnimation.SetActive(false);
                }
            });
            base.SetFinalize(cause);
        }
    }
}
