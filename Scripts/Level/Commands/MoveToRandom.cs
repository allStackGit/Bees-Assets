
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class MoveToRandom : Command
    {
        /*
        Sends the squad towards a random spot on the map
         */
        
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            PrepareDamageToSendEntries("closest");
            Vector2 position = Squad.GetPosition();
            Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
            SetAndMove(randomCoordinates);
            InvokeRepeating(nameof(Timer), ConfigData.CommandTimerFrequency, ConfigData.CommandTimerFrequency);


        }
        private void Timer()
        {
            if (Squad.HasReachedDestination)
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("Reached the random destination on the map");
            }
            Vector2 destination = GetDestination();
            SetAndMove(destination);
            Squad.Status = $"Moving to random destination: {destination}";

        }
    }
}