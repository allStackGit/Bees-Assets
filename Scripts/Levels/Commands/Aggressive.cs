
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Aggressive : Command
    {
        public bool IsComfortablyWithinRange;
        public bool HasTakenPosition;
        public int ConsecutiveTimesWithinRange = 0;
        private Coroutine _moveTowardsEnemiesCoroutine;
        private readonly List<Ship> _moveTowardsEnemiesShips = new List<Ship>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            OriginalQueue.Clear();
            TargetingQueue.Clear();

            if (!GetSquad().IsDead)
            {
                IsAttacking = true;
                if (IsHiveMindCommand)
                {
                    PrepareDamageToSendEntries();
                }
                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);

                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                    Level.AddTimer(TimeoutTimer);
                }
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            CommandFrequency = 3f;
            IsComfortablyWithinRange = false;
            ConsecutiveTimesWithinRange = 0;
            HasTakenPosition = false;
            _moveTowardsEnemiesCoroutine = null;
        }

        private void BeginMoveTowardsEnemies()
        {
            if (_moveTowardsEnemiesCoroutine == null && !IsDead)
            {
                _moveTowardsEnemiesCoroutine = StartCoroutine(MoveTowardsEnemiesAcrossFrames());
            }
        }

        private IEnumerator MoveTowardsEnemiesAcrossFrames()
        {
            _moveTowardsEnemiesShips.Clear();
            foreach (Ship ship in GetSquad().GetShips())
            {
                if (ship != null && !ship.IsDead)
                {
                    _moveTowardsEnemiesShips.Add(ship);
                }
            }

            for (int i = 0; i < _moveTowardsEnemiesShips.Count; i++)
            {
                if (IsDead || GetSquad().IsDead || EnemySquad == null || EnemySquad.IsDead)
                {
                    _moveTowardsEnemiesShips.Clear();
                    _moveTowardsEnemiesCoroutine = null;
                    yield break;
                }

                Ship ship = _moveTowardsEnemiesShips[i];
                Ship target = ship.SetAndGetTargetEnemy();
                if (target == null)
                {
                    _moveTowardsEnemiesShips.Clear();
                    _moveTowardsEnemiesCoroutine = null;
                    SetFinalize("No more enemy ships to target");
                    yield break;
                }

                ship.MoveToTrackedPoint(target.GetPosition());
                yield return null;
            }

            _moveTowardsEnemiesShips.Clear();
            _moveTowardsEnemiesCoroutine = null;
        }

        private void Timer()
        {
            FreezeDiagnostics.RecordAggressiveTick(Level);
            Squad squad = GetSquad();
            if (squad.IsDead)
            {
                return;
            }
            if (EnemySquad.IsDead)
            {
                SetFinalize("The enemy squad is gone or dead");
                return;
            }

            squad.Status = $"Targeting enemy squad #{EnemySquad.SquadNumber}";
            if (!IsComfortablyWithinRange)
            {
                if (squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                {
                    ConsecutiveTimesWithinRange++;
                    if (ConsecutiveTimesWithinRange == 3)
                    {
                        ConsecutiveTimesWithinRange = 0;
                        IsComfortablyWithinRange = true;
                    }
                }
                else
                {
                    ConsecutiveTimesWithinRange = 0;
                }
                BeginMoveTowardsEnemies();
                if (IsDead)
                {
                    return;
                }

                if (!IsCloseToTarget && squad.DistanceToPoint(EnemySquad.GetPosition()) < squad.MaxRange * 2)
                {
                    Level.CancelTimer(CommandTimer);
                    CommandFrequency = .25f;
                    IsCloseToTarget = true;
                    CommandTimer.Reuse(CommandFrequency, Timer, true);
                    Level.AddTimer(CommandTimer);
                }
                return;
            }

            bool allWithinRange = squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad);
            if (allWithinRange && (squad.MaxRange >= 45 || EnemySquad.IsDefenseless))
            {
                if (!HasTakenPosition)
                {
                    SetAndMove(squad.GetPosition());
                    HasTakenPosition = true;
                }
            }
            else
            {
                HasTakenPosition = false;
                IsComfortablyWithinRange = false;
            }
        }
    }
}
