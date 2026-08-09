
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Aggressive : Command
    {
        /// <summary>
        /// Are the ships comfortably within range of all of the enemy squad ships?
        /// </summary>
        public bool IsComfortablyWithinRange;
        /// <summary>
        /// Has the squad taken up a "standing" position comfortably within range?
        /// </summary>
        public bool HasTakenPosition;
        public int ConsecutiveTimesWithinRange = 0;

        /// <summary>
        ///  Sends the squad towards the enemy and follows them, attacking until one squad is dead
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
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
                    if (!IsComfortablyWithinRange) // check if all of their squad ships are comfortably within range of all of our squad ships
                    {
                        if (GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                        {
                            // Keep track of how many times all of the ships have been within range so we don't stop moving towards the enemy until we've been within range for a little bit at least
                            ConsecutiveTimesWithinRange++;
                            if (ConsecutiveTimesWithinRange == 3)
                            {
                                ConsecutiveTimesWithinRange = 0;
                                IsComfortablyWithinRange = true;
                                //Debug.Log($"We are comfortably within range");
                            }
                            //Debug.Log($"All ships are within range of the some ships in the enemy squad, we can stop moving towards them? {ConsecutiveTimesWithinRange}");
                        }
                        else
                        {
                            ConsecutiveTimesWithinRange = 0;
                        }
                        //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        //SetAndMove(Enemy.GetPosition());
                        MoveTowardsEnemies();

                        // Once we get close to the target we speed up the timer so we get more up to date information
                        if (!IsCloseToTarget && GetSquad().DistanceToPoint(EnemySquad.GetPosition()) < GetSquad().MaxRange * 2)
                        {
                            //Debug.Log($"{GetSquad().Name} is close to {EnemySquad.Name}");
                            Level.CancelTimer(CommandTimer);
                            //CancelInvoke(nameof(Timer));
                            CommandFrequency = .25f;
                            IsCloseToTarget = true;
                            CommandTimer.Reuse(CommandFrequency, Timer, true);
                            Level.AddTimer(CommandTimer);
                            //InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                        }
                    }
                    else if ((GetSquad().MaxRange >= 45 && GetSquad().AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad)) || (GetSquad().AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad) && EnemySquad.IsDefenseless))
                    {
                        //Debug.Log($"All ships are comfortably within range, we don't need to move."); 
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
                    //CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            
        }

        
    }
}
