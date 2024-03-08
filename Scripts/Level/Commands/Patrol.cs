
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Patrol : Command
    {
        /*
        The squad moves in a loop between predefined points
        */
        public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, Vector2 topLeft, Vector2 bottomRight)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            if (Squad != null)
            {
                Vector2 position = Squad.GetPosition();
                if (IsHiveMindCommand)
                {
                    topLeft = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, Vector2.one * 10);
                    bottomRight = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIPatrolMaxSize, Vector2.one * 10);
                    Invoke(nameof(FinishAIPatrol), ConfigData.Configuration.AISquadPatrolTime);
                }

                //Debug.Log($"topLeft: {topLeft}, bottomRight: {bottomRight}");

                Vector2 topRight = new Vector2(bottomRight.x, topLeft.y);
                Vector2 bottomLeft = new Vector2(topLeft.x, bottomRight.y);

                AddDestination(topLeft);
                AddDestination(topRight);
                AddDestination(bottomRight);
                AddDestination(bottomLeft);

                InvokeRepeating(nameof(Timer), .1f, .1f);
            }
            else
            {
                SetFinalize("The squad is dead");
            }
            
        }
        private void Timer()
        {
            if (Squad != null)
            {
                // check if squad has reached destination and if so, cancel the timer and start over again for the next destination
                Vector2 destination = GetDestination();
                if (Squad.HasReachedDestination)
                {

                    RemoveDestination(destination);
                    AddDestination(destination);
                }
                destination = GetDestination();
                Squad.Move(destination);
                Squad.Status = $"Patrolling towards {destination}";
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }
            
        }
        private void FinishAIPatrol()
        {
            CancelInvoke(nameof(Timer));
            SetFinalize("Finished Patrol");
        }
    }
}