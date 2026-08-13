
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class SwipeSquad : Command
    {
        private bool _gotToEnemy;
        private Vector2 _swipeDestination = Vector2.zero;

        public void Execute(ConfigData.CommandTypes commandType, ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            CommandType = commandType;
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            // Command.Setup() built this queue before base.Execute() installed the new
            // shooting strategy. Discard the stale ordering before pursuit begins.
            OriginalQueue.Clear();
            TargetingQueue.Clear();

            IsAttacking = true;
            PrepareDamageToSendEntries();
            CommandTimer.Reuse(CommandFrequency, SwipeSquadTimer, true, true);
            Level.AddTimer(CommandTimer);
            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                Level.AddTimer(TimeoutTimer);
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

        private void SwipeSquadTimer()
        {
            if (IsDead)
            {
                return;
            }

            if (EnemySquad.IsDead)
            {
                SetFinalize("The enemy squad is gone or dead");
                return;
            }

            GetSquad().Status = $"Targeting enemy squad {EnemySquad.Name} #{EnemySquad.Id} with {CommandType}";
            if (!_gotToEnemy && !GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
            {
                if (!GetSquad().GetShips().Any(ship => ship.IsPathfinding))
                {
                    MoveTowardsEnemies();
                }
            }
            else if (_swipeDestination == Vector2.zero)
            {
                _gotToEnemy = true;
                GetSquad().Status = $"Using {CommandType} against enemy squad {EnemySquad.Name} #{EnemySquad.Id}";
                _enemyPosition = EnemySquad.GetPosition();
                _angle = GetSquad().AngleToPoint(_enemyPosition);

                if (CommandType == ConfigData.CommandTypes.RightSwipe)
                {
                    _angle += .25f * Mathf.PI;
                    if (_angle > Mathf.PI)
                    {
                        _angle -= 2 * Mathf.PI;
                    }
                }
                else
                {
                    _angle -= .25f * Mathf.PI;
                    if (_angle < -Mathf.PI)
                    {
                        _angle += 2 * Mathf.PI;
                    }
                }

                _distance = EnemySquad.MaxRange * 2f;
                if (_distance < GetSquad().MaxRange - 2)
                {
                    _distance = GetSquad().MaxRange - 2;
                }
                _swipeDestination = EnemySquad.CirclePoint(_angle, _distance);
                SetAndMove(_swipeDestination);
            }
            else if (GetSquad().HasReachedDestination)
            {
                SetFinalize("Finished swiping past the enemy");
            }
        }
    }
}
