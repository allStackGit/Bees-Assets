
using System.Collections;
using System.Linq;
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

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            // Command.Setup() builds its initial movement-target queue before base.Execute()
            // installs the server-selected shooting strategy. Discard that stale ordering so
            // the first movement target is rebuilt under the strategy whose outcome is being learned.
            OriginalQueue.Clear();
            TargetingQueue.Clear();

            if (!GetSquad().IsDead)
            {
                IsAttacking = true;
                // Damage snapshots are learning bookkeeping for Hive Mind commands. User-issued
                // attacks have no server outcome IDs and should not synchronously walk the enemy
                // squad on the input frame merely to populate data that will never be stored.
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
            Ship[] ships = GetSquad().GetShips().Where(ship => ship != null && !ship.IsDead).ToArray();
            for (int i = 0; i < ships.Length; i++)
            {
                if (IsDead || GetSquad().IsDead || EnemySquad == null || EnemySquad.IsDead)
                {
                    _moveTowardsEnemiesCoroutine = null;
                    yield break;
                }

                Ship ship = ships[i];
                Ship target = ship.SetAndGetTargetEnemy();
                if (target == null)
                {
                    _moveTowardsEnemiesCoroutine = null;
                    SetFinalize("No more enemy ships to target");
                    yield break;
                }

                // Moving-target pursuit has different ownership from a fresh player movement
                // order. Keep active A*, a useful current path, and failed-search retry backoff
                // instead of replacing them every aggressive timer tick.
                ship.MoveToTrackedPoint(target.GetPosition());

                // Path-map snapshots and request startup are main-thread work even though the
                // actual path search runs on Task.Run. Spread squad attack startup over frames.
                yield return null;
            }

            _moveTowardsEnemiesCoroutine = null;
        }

        private void Timer()
        {
            FreezeDiagnostics.RecordAggressiveTick(Level);
            if (!GetSquad().IsDead)
            {
                if (!EnemySquad.IsDead)
                {
                    GetSquad().Status = $"Targeting enemy squad #{EnemySquad.SquadNumber}";
                    if (!IsComfortablyWithinRange)
                    {
                        if (GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
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

                        if (!IsCloseToTarget && GetSquad().DistanceToPoint(EnemySquad.GetPosition()) < GetSquad().MaxRange * 2)
                        {
                            Level.CancelTimer(CommandTimer);
                            CommandFrequency = .25f;
                            IsCloseToTarget = true;
                            CommandTimer.Reuse(CommandFrequency, Timer, true);
                            Level.AddTimer(CommandTimer);
                        }
                    }
                    else if ((GetSquad().MaxRange >= 45 && GetSquad().AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad)) ||
                             (GetSquad().AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad) && EnemySquad.IsDefenseless))
                    {
                        if (!HasTakenPosition)
                        {
                            SetAndMove(GetSquad().GetPosition());
                            HasTakenPosition = true;
                        }
                    }
                    else
                    {
                        HasTakenPosition = false;
                        IsComfortablyWithinRange = false;
                    }
                }
                else
                {
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
        }
    }
}
