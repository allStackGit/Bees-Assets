
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class InAndOut : Command
    {
        public Vector2 ReturnPoint;
        public bool HasReachedReturnPoint, HasReachedEnemySquad;
        private Vector2 _position, _enemyPosition;
        private float _distance;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            OriginalQueue.Clear();
            TargetingQueue.Clear();

            if (!EnemySquad.IsDead)
            {
                IsAttacking = true;
                PrepareDamageToSendEntries();
                _position = GetSquad().GetPosition();
                _enemyPosition = EnemySquad.GetPosition();
                _distance = GetSquad().DistanceToPoint(_enemyPosition);
                ReturnPoint = _distance > EnemySquad.MaxRange && _distance < 50
                    ? Utilities.RandomCoordinate(Level, _position, Vector2.one * 45, Vector2.zero)
                    : Utilities.RandomCoordinate(Level, _enemyPosition, Vector2.one * (EnemySquad.MaxRange + 45), Vector2.one * (EnemySquad.MaxRange + 10));

                CommandTimer.Reuse(CommandFrequency, InAndOutTimer, true, true);
                Level.AddTimer(CommandTimer);

                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                    Level.AddTimer(TimeoutTimer);
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
            ReturnPoint = Vector2.zero;
            HasReachedReturnPoint = false;
            HasReachedEnemySquad = false;
        }

        private bool MoveTowardsEnemiesTracked()
        {
            List<Ship> ships = GetSquad().GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                Ship target = ship.SetAndGetTargetEnemy();
                if (target == null)
                {
                    return false;
                }
                ship.MoveToTrackedPoint(target.GetPosition());
            }
            return true;
        }

        private void InAndOutTimer()
        {
            if (IsDead || GetSquad().IsDead)
            {
                return;
            }

            if (EnemySquad.IsDead)
            {
                SetFinalize("The enemy squad is gone or dead");
                return;
            }

            if (!HasReachedEnemySquad)
            {
                if (!GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                {
                    if (!Stage.IsTraining)
                    {
                        GetSquad().Status = $"Targeting enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    }
                    if (!MoveTowardsEnemiesTracked())
                    {
                        SetFinalize("No more enemy ships to target");
                        return;
                    }
                }
                else
                {
                    HasReachedEnemySquad = true;
                    if (!Stage.IsTraining)
                    {
                        GetSquad().Status = $"Retreating away from enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    }
                    HasReachedReturnPoint = false;
                    SetAndMove(ReturnPoint);
                    ReturnPoint = GetDestination();
                }
            }
            else if (GetSquad().HasReachedDestination)
            {
                SetFinalize("Returned to starting point");
            }
        }
    }
}