
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Aggressive : Command
    {
        public bool IsComfortablyWithinRange;
        public int ConsecutiveTimesWithinRange = 0;
        /// <summary>
        ///  Sends the squad towards the enemy and follows them, attacking until one squad is dead
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            if (!Squad.IsDead)
            {
                IsAttacking = true;
                PrepareDamageToSendEntries();
                InvokeRepeating(nameof(Timer), .1f, CommandFrequency);
            }
            
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (Enemy != null && !Enemy.IsDead)
                {
                    Squad.Status = $"Targeting enemy squad #{Enemy.SquadNumber}";
                    if (!IsComfortablyWithinRange) // check if all of their squad ships are comfortably within range of all of our squad ships
                    {
                        if (Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy))
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
                        //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        //SetAndMove(Enemy.GetPosition());
                        MoveTowardsEnemies();
                        if (!IsCloseToTarget && Squad.DistanceToPoint(Enemy.GetPosition()) < Squad.MaxRange * 2)
                        {
                            Debug.Log($"{Squad.Name} is close to {Enemy.Name}");
                            CancelInvoke(nameof(Timer));
                            CommandFrequency = .25f;
                            IsCloseToTarget = true;
                            InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                        }
                    }
                    else if (Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy))
                    {
                        Debug.Log($"All ships are comfortably within range, we don't need to move.");
                    }
                    else
                    {
                        IsComfortablyWithinRange = false;
                    }
                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            
        }
    }
}