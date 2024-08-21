
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class SwipeSquad : Command
    {
        /*
        Sends the squad to go past the enemy at an angle just within the squad's range and then end at a distance just out of the enemy's range.
        */

        private bool _gotToEnemy;
        private Vector2 _swipeDestination = Vector2.zero;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            IsAttacking = true;
            PrepareDamageToSendEntries();
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (EnemySquad != null && !EnemySquad.IsDead)
                {
                    Squad.Status = $"Targeting enemy squad {EnemySquad.Name} #{EnemySquad.Id} with {Strategy.Name}";

                    if (!_gotToEnemy && !Squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad)) // if we haven't reached the enemy yet
                    {
                        //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        //SetAndMove(Enemy.GetPosition());
                        MoveTowardsEnemies();
                    }
                    else if (_swipeDestination == Vector2.zero) // if we just reached the enemy but haven't set where to swipe off to
                    {
                        //Debug.Log($"Reached the enemy! We are {Squad.DistanceTo(Enemy.GetPosition())} away from enemy, Reached: {Squad.IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Enemy)}");
                        _gotToEnemy = true;
                        //SetFinalize("Reached the enemy");
                        //return;
                        Squad.Status = $"Using {Strategy.Name} against enemy squad {EnemySquad.Name} #{EnemySquad.Id}";
                        Vector2 enemyPosition = EnemySquad.GetPosition();
                        float angle = Squad.AngleToPoint(enemyPosition);

                        if (Strategy.Name == "Right Swipe")
                        {
                            angle += .25f * Mathf.PI;

                            if (angle > Mathf.PI) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
                            {
                                angle -= 2 * Mathf.PI;
                            }

                        }
                        else
                        {
                            angle -= .25f * Mathf.PI;

                            if (angle < -Mathf.PI) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
                            {
                                angle += 2 * Mathf.PI;
                            }

                        }



                        float distance = EnemySquad.MaxRange * 2f;
                        if (distance < Squad.MaxRange - 2)
                        {
                            distance = Squad.MaxRange - 2;
                        }
                        _swipeDestination = Squad.CirclePoint(angle, distance);


                        SetAndMove(_swipeDestination);
                    }
                    else if (Squad.HasReachedDestination) // if we've reached the swiping destination
                    {
                        CancelInvoke(nameof(Timer));
                        SetFinalize("Finished swiping past the enemy");
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