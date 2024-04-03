
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Aggressive : Command
    {

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
                InvokeRepeating(nameof(Timer), ConfigData.CommandTimerFrequency, ConfigData.CommandTimerFrequency);
            }
            
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (Enemy != null && !Enemy.IsDead)
                {
                    Squad.Status = $"Targeting enemy squad #{Enemy.SquadNumber}";
                    if (!Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy)) // check if all of their squad ships are within range of all of our squad ships
                    {
                        //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        //SetAndMove(Enemy.GetPosition());
                        MoveTowardsEnemies();
                    }
                    else
                    {
                        //Debug.Log($"All ships are within range, we don't need to move.");
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