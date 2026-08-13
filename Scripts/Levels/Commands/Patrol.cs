
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Patrol : Command
    {
        private Vector2 _position, _topRight, _bottomLeft;
        private Vector2 _ten = Vector2.one * 10;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, Vector2 topLeft, Vector2 bottomRight)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

            _position = GetSquad().GetPosition();
            if (IsHiveMindCommand)
            {
                topLeft = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, _ten);
                bottomRight = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, _ten);
            }

            _topRight = new Vector2(bottomRight.x, topLeft.y);
            _bottomLeft = new Vector2(topLeft.x, bottomRight.y);

            AddDestination(topLeft);
            AddDestination(_topRight);
            AddDestination(bottomRight);
            AddDestination(_bottomLeft);

            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
            Level.AddTimer(CommandTimer);

            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(ConfigData.Configuration.AISquadPatrolTime, Timeout);
                Level.AddTimer(TimeoutTimer);
            }
        }

        private Vector2 _destination;
        private void Timer()
        {
            Squad squad = GetSquad();
            if (squad.IsDead)
            {
                return;
            }

            _destination = GetDestination();
            if (squad.HasReachedDestination)
            {
                RemoveDestination(_destination);
                AddDestination(_destination);
                _destination = GetDestination();
                squad.Move(_destination);
            }
            squad.Status = $"Patrolling towards {_destination}";
        }
    }
}
