
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class CircleSquad : Command
    {
        private bool _gotToEnemy, _hasSetIdealDistance;
        private float _idealDistance, _angle;
        private Vector2 _lastDestination;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            IsAttacking = true;
            PrepareDamageToSendEntries();
            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
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
            CommandFrequency = 3f;
            _gotToEnemy = false;
            _hasSetIdealDistance = false;
            _idealDistance = 0;
            _angle = 0;
            _lastDestination = Vector2.zero;
        }

        private static bool IsAnyShipPathfinding(Squad squad)
        {
            List<Ship> ships = squad.GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].IsPathfinding)
                {
                    return true;
                }
            }
            return false;
        }

        private Vector2 _timer_squadPosition;
        private Vector2 _timer_destination;
        private int _timer_loops;
        private float _timer_angle;

        private void Timer()
        {
            Squad squad = GetSquad();
            if (squad.IsDead)
            {
                return;
            }
            if (EnemySquad.IsDead)
            {
                SetFinalize("The enemy squad is gone or dead");
                return;
            }

            squad.Status = $"Moving to circle enemy squad #{EnemySquad.SquadNumber}";
            if (!_gotToEnemy && !squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
            {
                squad.Status = $"Trying to get to a good circling position against {EnemySquad.Name}";
                if (!IsAnyShipPathfinding(squad))
                {
                    SetAndMove(EnemySquad.GetPosition());
                }
                return;
            }

            _timer_squadPosition = squad.GetPosition();
            if (!_hasSetIdealDistance)
            {
                _idealDistance = Mathf.Max(EnemySquad.DistanceToPoint(_timer_squadPosition), squad.MaxRange * .5f);
                _angle = EnemySquad.AngleToPoint(_timer_squadPosition) - (Mathf.PI * .5f);
                _hasSetIdealDistance = true;
            }
            if (!_gotToEnemy)
            {
                Level.CancelTimer(CommandTimer);
                CommandFrequency = .25f;
                _gotToEnemy = true;
                CommandTimer.Reuse(CommandFrequency, Timer, true);
                Level.AddTimer(CommandTimer);
            }

            _timer_angle = EnemySquad.AngleToPoint(_timer_squadPosition);
            _angle = _timer_angle + (.06f * Mathf.PI);
            squad.Status = $"Circling enemy squad {EnemySquad.Name} at {_idealDistance} away";

            _timer_destination = EnemySquad.CirclePoint(_angle, _idealDistance);
            _timer_loops = 0;
            while (Vector2.Distance(_timer_destination, _lastDestination) < 1.25f && _timer_loops < 100)
            {
                _timer_loops++;
                _angle += (.06f * Mathf.PI);
                _timer_destination = EnemySquad.CirclePoint(_angle, _idealDistance);
            }

            _lastDestination = _timer_destination;
            if (!IsAnyShipPathfinding(squad))
            {
                SetAndMove(_timer_destination);
            }
        }
    }
}
