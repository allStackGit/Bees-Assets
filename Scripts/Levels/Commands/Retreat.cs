
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Retreat : Command
    {
        /*
         * Method for the Defensive strategy. The squad moves away from the enemy at a faster speed than it can normally move, but it can't fire while retreating
         */
        private Vector2 _retreatPoint, _enemyPosition, _position;
        private float _distance, _idealDistance, _angle;
        public override void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);

            if (HasSameEnemy())
            {
                _enemyPosition = EnemySquad.GetPosition();
                _distance = GetSquad().DistanceToPoint(_enemyPosition);
                _idealDistance = EnemySquad.MaxRange * 2;

                if (_distance < _idealDistance)
                {
                    _angle = GetSquad().AngleToPoint(_enemyPosition);
                    //Squad.IsRetreating = true;
                    GetSquad().Status = $"Retreating away from {EnemySquad.Name}";
                    _position = GetSquad().GetPosition();
                    _retreatPoint = new Vector2((Mathf.Sin(_angle) * (_idealDistance - _distance) + _position.x), (Mathf.Cos(_angle) * (_idealDistance - _distance) + _position.y));
                    SetAndMove(_retreatPoint);
                    InvokeRepeating(nameof(Timer), 0, CommandFrequency);
                }
                else
                {
                    Invoke(nameof(DelaySetFinalize), 3f);
                }
            }
            else
            {
                SetFinalize("The enemy squad is gone or dead");
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            _retreatPoint = Vector2.zero;
        }
        private void Timer()
        {
            if (GetSquad().HasReachedDestination)
            {
                CancelInvoke(nameof(Timer));
                SetFinalize($"Retreating and got far enough away.");
            }
            else
            {
                SetAndMove(_retreatPoint);
            }

        }
        private void DelaySetFinalize()
        {
            SetFinalize($"Retreating and already far enough away.");
        }
    }
}

