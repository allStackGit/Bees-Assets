
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Retreat : Command
    {
        private Vector2 _retreatPoint, _enemyPosition, _position;
        private float _distance, _idealDistance, _angle;
        private ScaledTimer _delayedSetFinalizeTimer = new ScaledTimer();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }
            if (!EnemySquad.IsDead)
            {
                _enemyPosition = EnemySquad.GetPosition();
                _distance = GetSquad().DistanceToPoint(_enemyPosition);
                _idealDistance = EnemySquad.MaxRange * 2;

                if (_distance < _idealDistance)
                {
                    _angle = GetSquad().AngleToPoint(_enemyPosition);
                    GetSquad().Status = $"Retreating away from {EnemySquad.Name}";
                    _position = GetSquad().GetPosition();
                    _retreatPoint = new Vector2((Mathf.Sin(_angle) * (_idealDistance - _distance) + _position.x), (Mathf.Cos(_angle) * (_idealDistance - _distance) + _position.y));
                    SetAndMove(_retreatPoint);

                    CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                    Level.AddTimer(CommandTimer);
                }
                else
                {
                    GetSquad().StopMoving();
                    _delayedSetFinalizeTimer.Reuse(3f, DelaySetFinalize);
                    Level.AddTimer(_delayedSetFinalizeTimer);
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
                SetFinalize("Retreating and got far enough away.");
            }
        }

        private void DelaySetFinalize()
        {
            SetFinalize("Retreating and already far enough away.");
        }

        public override void SetFinalize(string cause)
        {
            Level.CancelTimer(_delayedSetFinalizeTimer);
            base.SetFinalize(cause);
        }
    }
}
