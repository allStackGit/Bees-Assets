
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class MoveToRandom : Command
    {
        /*
        Sends the squad towards a random spot on the map
         */
        
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(ConfigData.CommandTypes.MoveToRandom, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy); 

            PrepareDamageToSendEntries("closest");
            Vector2 position = Squad.GetPosition();
            Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
            SetAndMove(randomCoordinates);
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);


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