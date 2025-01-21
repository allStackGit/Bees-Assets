
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class CircleSquad : Command
    {
        /*
        Sends the squad to circle clockwise around the enemy just within the squad's range until the enemy or the squad is killed
         */
        private bool _gotToEnemy, _hasSetIdealDistance;
        private float _idealDistance, _angle;
        private Vector2 _lastDestination;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            IsAttacking = true;
            PrepareDamageToSendEntries();
            InvokeRepeating(nameof(Timer), .1f, CommandFrequency);
            if (IsHiveMindCommand)
            {
                Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (EnemySquad != null && !EnemySquad.IsDead)
                {
                    Squad.Status = $"Moving to circle enemy squad #{EnemySquad.SquadNumber}";
                    if (!_gotToEnemy && !Squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                    {
                        //Debug.Log($"{Squad.Name} is trying to get to a good circling position against {Enemy.Name}");
                        Squad.Status = $"Trying to get to a good circling position against {EnemySquad.Name}";
                        SetAndMove(EnemySquad.GetPosition());
                        //MoveTowardsEnemies();
                    }
                    else
                    {
                        Vector2 squadPosition = Squad.GetPosition();
                        if (!_hasSetIdealDistance)
                        {
                            _idealDistance = EnemySquad.DistanceToPoint(squadPosition);
                            _angle = EnemySquad.AngleToPoint(squadPosition) - (Mathf.PI * .5f);
                            _hasSetIdealDistance = true;
                        }
                        if (!_gotToEnemy)
                        {
                            CancelInvoke(nameof(Timer));
                            CommandFrequency = .1f;
                            _gotToEnemy = true;
                            InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                        }
                        float angle = EnemySquad.AngleToPoint(squadPosition);

                        _angle = angle + (.06f * Mathf.PI);
                        //Debug.Log($"{Squad.Name} is circling enemy squad # {Enemy.Name} at {_idealDistance} away");
                        Squad.Status = $"Circling enemy squad # {EnemySquad.Name} at {_idealDistance} away";

                        Vector2 destination = EnemySquad.CirclePoint(_angle, _idealDistance);
                        int loops = 0;
                        while (Vector2.Distance(destination, _lastDestination) < .15f && loops < 100)
                        {
                            loops++;
                            //Debug.Log($"Next squad position for {Squad.Name} is too close: {Vector2.Distance(destination, _lastDestination)}");
                            _angle += (.06f * Mathf.PI);
                            destination = EnemySquad.CirclePoint(_angle, _idealDistance);
                        }
                        _lastDestination = destination;
                        SetAndMove(destination);
                    }
                }
                else
                {
                    //Debug.Log("The enemy is dead or does not exist.");
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
           

        }
    }
}