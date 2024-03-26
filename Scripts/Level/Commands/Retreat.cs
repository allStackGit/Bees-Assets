
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Retreat : Command
    {
        /*
         * Method for the Defensive strategy. The squad moves away from the enemy at a faster speed than it can normally move, but it can't fire while retreating
         */
        private Vector2 _retreatPoint;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);



            if (Enemy != null && !Enemy.IsDead)
            {
                double distance = Squad.DistanceTo(Enemy.GetPosition());
                double idealDistance = Enemy.MaxRange * 1.5;

                if (distance < idealDistance)
                {
                    float angle = Squad.AngleToPoint(Enemy.GetPosition());
                    Squad.IsRetreating = true;
                    Squad.Status = $"Retreating away from {Enemy.Name}";
                    Vector2 position = Squad.GetPosition();
                    _retreatPoint = new Vector2((float)(Mathf.Sin(angle) * (idealDistance - distance) + position.x), (float)(Mathf.Cos(angle) * (idealDistance - distance) + position.y));
                    SetAndMove(_retreatPoint);
                    InvokeRepeating(nameof(Timer), ConfigData.CommandTimerFrequency, ConfigData.CommandTimerFrequency);
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
        private void Timer()
        {
            if (Squad.HasReachedDestination)
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

