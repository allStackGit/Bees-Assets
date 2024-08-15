
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class CircleSquad : Command
    {
        /*
        Sends the squad to circle clockwise around the enemy just within the squad's range until the enemy or the squad is killed
         */
        private bool _gotToEnemy, _hasSetIdealDistance;
        private float _idealDistance, _angle;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            IsAttacking = true;
            PrepareDamageToSendEntries();
            InvokeRepeating(nameof(Timer), .1f, CommandFrequency);
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (EnemySquad != null && !EnemySquad.IsDead)
                {
                    Squad.Status = $"Moving to circle enemy squad #{EnemySquad.SquadNumber}";
                    Vector2 squadPosition = Squad.GetPosition();
                    if (!_gotToEnemy && !Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                    {
                        //Debug.Log($"{Squad.Name} is trying to get to a good circling position against {Enemy.Name}");
                        Squad.Status = $"Trying to get to a good circling position against {EnemySquad.Name}";
                        SetAndMove(EnemySquad.GetPosition());
                        //MoveTowardsEnemies();
                    }
                    else
                    {
                        if (!_hasSetIdealDistance)
                        {
                            _idealDistance = EnemySquad.DistanceToPoint(squadPosition) * .97f;
                            _angle = EnemySquad.AngleToPoint(squadPosition) - (Mathf.PI * .5f);
                            _hasSetIdealDistance = true;
                        }

                        _gotToEnemy = true;
                        float angle = EnemySquad.AngleToPoint(squadPosition);

                        _angle = angle + (.06f * Mathf.PI);
                        //Debug.Log($"{Squad.Name} is circling enemy squad # {Enemy.Name} at {_idealDistance} away");
                        Squad.Status = $"Circling enemy squad # {EnemySquad.Name} at {_idealDistance} away";
                        NewCircleSpot();
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
        private void NewCircleSpot()
        {
            Vector2 destination = EnemySquad.CirclePoint(_angle, _idealDistance);
            //Debug.Log($"Current Position: {Squad.GetPosition()}, Next Destination: {destination}");
            SetAndMove(destination);
        }
    }
}