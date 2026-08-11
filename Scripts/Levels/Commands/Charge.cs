using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Charge : Command
    {
        public HashSet<Ship> ChargingShips = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public bool IsCharging;

        private ConfigData.ShootingStrategyTypes _execute_shootingStrategy;
        private long _execute_commandOutcomeId;
        private long _execute_shootingStrategyOutcomeId;
        private List<Ship> _execute_ships;
        private int _execute_loopIndex;
        private Ship _execute_currentShip;
        private Barge _execute_barge;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            _execute_shootingStrategy = shootingStrategy;
            _execute_commandOutcomeId = commandOutcomeId;
            _execute_shootingStrategyOutcomeId = shootingStrategyOutcomeId;

            base.Execute(_execute_shootingStrategy, _execute_commandOutcomeId, _execute_shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            // Initial Charge target selection below uses the live weapon strategy correctly,
            // but later pursuit can fall back to Command.TargetingQueue. Remove the queue that
            // Setup built under the previous shooting strategy so that fallback is also current.
            OriginalQueue.Clear();
            TargetingQueue.Clear();

            IsAttacking = true;
            GetSquad().Status = $"Starting charging run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();

            _execute_ships = GetSquad().GetShips();
            for (_execute_loopIndex = 0; _execute_loopIndex < _execute_ships.Count && !IsDead; _execute_loopIndex++)
            {
                _execute_currentShip = _execute_ships[_execute_loopIndex];
                if (_execute_currentShip.ShipType == ConfigData.ShipTypes.Barge)
                {
                    _execute_barge = (Barge)_execute_currentShip;
                    _execute_barge.HasCompletedRun = false;
                    _execute_barge.ShipsHit.Clear();
                }
                GetTargetShip(_execute_currentShip);
            }
            if (!IsDead)
            {
                CommandTimer.Reuse(CommandFrequency, Timer, true);
                Level.AddTimer(CommandTimer);
                TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                Level.AddTimer(TimeoutTimer);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            ChargingShips.Clear();
            IsCharging = false;
        }

        private Bomb _getTargetShip_bomb;
        private List<Ship> _getTargetShip_targetingList;

        private void GetTargetShip(Ship chargingShip)
        {
            _getTargetShip_bomb = (Bomb)chargingShip.Weapons.First();
            _getTargetShip_targetingList = _getTargetShip_bomb.MakeSortedTargetingList(true);
            if (_getTargetShip_targetingList.Count > 0)
            {
                if (!_getTargetShip_bomb.DetermineTargetShip(_getTargetShip_targetingList, true))
                {
                    _getTargetShip_targetingList = _getTargetShip_bomb.MakeSortedTargetingList(true);
                    if (_getTargetShip_targetingList.Count > 0)
                    {
                        _getTargetShip_bomb.SetRandomTarget(_getTargetShip_targetingList);
                    }
                }
                chargingShip.TargetEnemyShipToFollow = _getTargetShip_bomb.TargetShip;
            }
            else
            {
                SetFinalize("No more enemy ships to target");
            }
        }

        private bool SendShipToTarget(Ship ship)
        {
            Ship target = ship.SetAndGetTargetEnemy();
            if (target == null)
            {
                SetFinalize("No more enemy ships to target");
                return false;
            }
            ship.MoveToPoint(target.GetPosition());
            return true;
        }
        private bool HaveAllShipsFinished(List<Barge> ships)
        {
            // Charge owns the whole squad's run. Completing one Barge must not finalize the
            // command and ResetCharge() the other Barges while they are still charging/cooling.
            return ships.Count > 0 && ships.All((ship) => ship.HasCompletedRun);
        }
        private bool ShouldShipPursueTarget(Barge ship)
        {
            return !ship.HasStartedCharging && !ship.HasCompletedRun && ship.HasTargetEnemyShipToFollow;
        }

        private bool HasTargetsWithinChargingRange(Barge barge)
        {
            return barge.Charge.HasTargetShip && Utilities.IsRotatedTowards(barge, barge.GetDegreesTowardsPoint(barge.Charge.TargetShip.GetPosition())) &&
            !Utilities.HasObstaclesInTheWay(barge.GetPosition(), barge.Charge.TargetShip.GetPosition()) && barge.ShipsWithinRange.Contains(barge.Charge.TargetShip);
        }

        private List<Barge> _timer_barges;
        private int _timer_index;

        private void Timer()
        {
            if (IsDead || GetSquad().IsDead)
            {
                return;
            }

            if (EnemySquad.IsDead)
            {
                SetFinalize("The enemy squad is gone or dead");
                return;
            }

            _timer_barges = GetSquad().GetShips().Select((ship) => (Barge)ship).ToList();
            for (_timer_index = 0; _timer_index < _timer_barges.Count && !IsDead; _timer_index++)
            {
                Barge barge = _timer_barges[_timer_index];
                if (ShouldShipPursueTarget(barge))
                {
                    if (HasTargetsWithinChargingRange(barge))
                    {
                        if (!ChargingShips.Contains(barge))
                        {
                            Debug.Log($"Barge is charging after {barge.Charge.TargetShip} which is within range");
                            ChargingShips.Add(barge);
                            IsCharging = true;
                            StartCoroutine(barge.ChargeForward(barge.Charge.TargetShip));
                        }
                    }
                    else if (!SendShipToTarget(barge))
                    {
                        break;
                    }
                }
                else if (!barge.Charge.HasTargetShip)
                {
                    GetTargetShip(barge);
                }
            }

            if (!IsDead && HaveAllShipsFinished(_timer_barges))
            {
                SetFinalize("Completed charging run");
            }
        }

        public override void SetFinalize(string cause)
        {
            _timer_barges = GetSquad().GetShips().Select((ship) => (Barge)ship).ToList();
            _timer_barges.ForEach((barge) =>
            {
                barge.ResetCharge();
            });

            base.SetFinalize(cause);
        }
    }
}
