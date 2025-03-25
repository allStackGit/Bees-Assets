
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
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            IsAttacking = true;
            PrepareDamageToSendEntries();
            Timer();
            if (!IsDead)
            {
                CommandTimer.Reuse(CommandFrequency, Timer, true);
                Level.AddTimer(CommandTimer);
            }

            //InvokeRepeating(nameof(Timer), .1f, CommandFrequency);
            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                Level.AddTimer(TimeoutTimer);
                //Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            _gotToEnemy = false;
            _hasSetIdealDistance = false;
            _idealDistance = 0;
            _angle = 0;
            _lastDestination = Vector2.zero;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Timer() method:
        //////////////////////////////////////////////////////////////////////////////

        private Vector2 _timer_squadPosition;
        private Vector2 _timer_destination;
        private int _timer_loops;
        private float _timer_angle;

        private void Timer()
        {
            if (!GetSquad().IsDead)
            {
                if (!EnemySquad.IsDead)
                {
                    GetSquad().Status = $"Moving to circle enemy squad #{EnemySquad.SquadNumber}";
                    if (!_gotToEnemy && !GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                    {
                        //Debug.Log($"{Squad.Name} is trying to get to a good circling position against {Enemy.Name}");
                        GetSquad().Status = $"Trying to get to a good circling position against {EnemySquad.Name}";
                        SetAndMove(EnemySquad.GetPosition());
                        //MoveTowardsEnemies();
                    }
                    else
                    {
                        _timer_squadPosition = GetSquad().GetPosition();
                        if (!_hasSetIdealDistance)
                        {
                            _idealDistance = EnemySquad.DistanceToPoint(_timer_squadPosition);
                            _angle = EnemySquad.AngleToPoint(_timer_squadPosition) - (Mathf.PI * .5f);
                            _hasSetIdealDistance = true;
                        }
                        if (!_gotToEnemy)
                        {
                            Level.CancelTimer(CommandTimer);
                            //CancelInvoke(nameof(Timer));
                            CommandFrequency = .1f;
                            _gotToEnemy = true;
                            CommandTimer.Reuse(CommandFrequency, Timer, true);
                            Level.AddTimer(CommandTimer);
                            //InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                        }

                        _timer_angle = EnemySquad.AngleToPoint(_timer_squadPosition);
                        _angle = _timer_angle + (.06f * Mathf.PI);

                        //Debug.Log($"{Squad.Name} is circling enemy squad # {Enemy.Name} at {_idealDistance} away");
                        GetSquad().Status = $"Circling enemy squad {EnemySquad.Name} at {_idealDistance} away";

                        _timer_destination = EnemySquad.CirclePoint(_angle, _idealDistance);
                        _timer_loops = 0;

                        while (Vector2.Distance(_timer_destination, _lastDestination) < .15f && _timer_loops < 100)
                        {
                            _timer_loops++;
                            //Debug.Log($"Next squad position for {Squad.Name} is too close: {Vector2.Distance(destination, _lastDestination)}");
                            _angle += (.06f * Mathf.PI);
                            _timer_destination = EnemySquad.CirclePoint(_angle, _idealDistance);
                        }

                        _lastDestination = _timer_destination;
                        SetAndMove(_timer_destination);
                    }
                }
                else
                {
                    //Debug.Log("The enemy is dead or does not exist.");
                    //CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
        }

    }
}