
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class SwipeSquad : Command
    {
        /*
        Sends the squad to go past the enemy at an angle just within the squad's range and then end at a distance just out of the enemy's range.
        */

        private bool _gotToEnemy;
        private Vector2 _swipeDestination = Vector2.zero;
        public override void Execute(ConfigData.CommandTypes commandType, ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(commandType, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);

            IsAttacking = true;
            PrepareDamageToSendEntries();
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            if (IsHiveMindCommand)
            {
                Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            _gotToEnemy = false;
            _swipeDestination = Vector2.zero;
        }
        private Vector2 _enemyPosition;
        private float _angle, _distance;
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (!EnemySquad.IsDead)
                {
                    Squad.Status = $"Targeting enemy squad {EnemySquad.Name} #{EnemySquad.Id} with {Strategy.CommandType}";

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
                        Squad.Status = $"Using {Strategy.CommandType} against enemy squad {EnemySquad.Name} #{EnemySquad.Id}";
                        _enemyPosition = EnemySquad.GetPosition();
                        _angle = Squad.AngleToPoint(_enemyPosition);

                        if (Strategy.CommandType == ConfigData.CommandTypes.RightSwipe)
                        {
                            _angle += .25f * Mathf.PI;

                            if (_angle > Mathf.PI) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
                            {
                                _angle -= 2 * Mathf.PI;
                            }

                        }
                        else
                        {
                            _angle -= .25f * Mathf.PI;

                            if (_angle < -Mathf.PI) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
                            {
                                _angle += 2 * Mathf.PI;
                            }

                        }



                        _distance = EnemySquad.MaxRange * 2f;
                        if (_distance < Squad.MaxRange - 2)
                        {
                            _distance = Squad.MaxRange - 2;
                        }
                        _swipeDestination = Squad.CirclePoint(_angle, _distance);


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