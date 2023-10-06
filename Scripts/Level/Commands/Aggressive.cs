
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Aggressive : Command
    {
        /*
        Sends the squad towards the enemy and follows them, attacking until one squad is dead
         */
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            if (Squad != null && !Squad.IsDead)
            {
                IsAttacking = true;
                PrepareDamageToSendEntries();
                InvokeRepeating(nameof(Timer), .1f, .1f);
            }
            else
            {
                SetFinalize("The squad is dead");
            }
            
        }
        private void Timer()
        {
            if (Squad != null && !Squad.IsDead)
            {
                if (Enemy != null && !Enemy.IsDead)
                {
                    Squad.Status = $"Targeting enemy squad #{Enemy.SquadNumber}";
                    if (!Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy)) // check if all of their squad ships are within range of all of our squad ships
                    {
                        //Debugger.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        SetAndMove(Enemy.GetPosition());
                    }
                    else
                    {
                        //Debugger.Log($"All ships are within range, we don't need to move.");
                    }
                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }
            
        }
    }
}