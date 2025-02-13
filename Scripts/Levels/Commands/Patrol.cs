
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Patrol : Command
    {
        private Vector2 _position, _topRight, _bottomLeft;
        private Vector2 _ten = Vector2.one * 10;
        /// <summary>
        /// The squad moves in a loop between predefined points
        /// </summary>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="shootingStrategyOutcomeId"></param>
        /// <param name="noEnemy"></param>
        /// <param name="topLeft"></param>
        /// <param name="bottomRight"></param>
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy, Vector2 topLeft, Vector2 bottomRight)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);

            _position = Squad.GetPosition();
            if (IsHiveMindCommand)
            {
                topLeft = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, _ten);
                bottomRight = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, _ten);
                Invoke(nameof(FinishAIPatrol), ConfigData.Configuration.AISquadPatrolTime);
            }

            //Debug.Log($"topLeft: {topLeft}, bottomRight: {bottomRight}");

            _topRight = new Vector2(bottomRight.x, topLeft.y);
            _bottomLeft = new Vector2(topLeft.x, bottomRight.y);

            AddDestination(topLeft);
            AddDestination(_topRight);
            AddDestination(bottomRight);
            AddDestination(_bottomLeft);

            InvokeRepeating(nameof(Timer), 0, CommandFrequency);

        }
        private Vector2 _destination;
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                // check if squad has reached destination and if so, cancel the timer and start over again for the next destination
                _destination = GetDestination();
                if (Squad.HasReachedDestination)
                {

                    RemoveDestination(_destination);
                    AddDestination(_destination);
                }
                _destination = GetDestination();
                Squad.Move(_destination);
                Squad.Status = $"Patrolling towards {_destination}";
            }
        }
        private void FinishAIPatrol()
        {
            CancelInvoke(nameof(Timer));
            SetFinalize("Finished Patrol");
        }
    }
}