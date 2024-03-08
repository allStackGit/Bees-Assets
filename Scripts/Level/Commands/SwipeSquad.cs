
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

            if (Squad != null)
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
            if (Enemy != null && !Enemy.IsDead && !Squad.IsDead)
            {
                Squad.Status = $"Targeting enemy squad {Enemy.Name} #{Enemy.Id} with {Strategy.Name}";

                if (!_gotToEnemy && !Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy)) // if we haven't reached the enemy yet
                {
                    //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                    SetAndMove(Enemy.GetPosition());
                }
                else if (_swipeDestination == Vector2.zero) // if we just reached the enemy but haven't set where to swipe off to
                {
                    //Debug.Log($"Reached the enemy! We are {Squad.DistanceTo(Enemy.GetPosition())} away from enemy, Reached: {Squad.IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Enemy)}");
                    _gotToEnemy = true;
                    //SetFinalize("Reached the enemy");
                    //return;
                    Squad.Status = $"Using {Strategy.Name} against enemy squad {Enemy.Name} #{Enemy.Id}";
                    Vector2 enemyPosition = Enemy.GetPosition();
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



                    float distance = Enemy.Range * 1.5f;
                    if (distance < Squad.Range - 2)
                    {
                        distance = Squad.Range - 2;
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