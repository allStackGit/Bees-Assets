
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Aggressive : Command
    {
        public bool IsComfortablyWithinRange;
        public bool HasTakenPosition;
        public int ConsecutiveTimesWithinRange = 0;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            if (!GetSquad().IsDead)
            {
                IsAttacking = true;
                PrepareDamageToSendEntries();
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
        }

        private void Timer()
        {
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
                        MoveTowardsEnemies();

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