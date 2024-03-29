
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Scouting : Command
    {
        /*
        Sends the squad towards a random spot on the map in search of ships
         */
        private bool _foundShips;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            PrepareDamageToSendEntries("closest");
            Vector2 position = Squad.GetPosition();
            Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
            SetAndMove(randomCoordinates);
            InvokeRepeating(nameof(Timer), ConfigData.CommandTimerFrequency, 5);

            Squad.Status = $"Moving to random destination to look for ships: {randomCoordinates}";
            Invoke(nameof(EndCommand), ConfigData.Configuration.AISquadPatrolTime);


        }
        private void Timer()
        {
            if (Squad.HasReachedDestination)
            {
                Vector2 position = Squad.GetPosition();
                Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
                SetAndMove(randomCoordinates);
                Squad.Status = $"Moving to random destination to look for ships: {randomCoordinates}";

            }

        }

        public void FoundShips()
        {
            _foundShips = true;
            Invoke(nameof(EndCommand), 5);
        }

        private void EndCommand()
        {
            if (_foundShips)
            {
                //Debug.Log($"Ending scouting command for {Squad.Name} because we found ships");
                SetFinalize("Found ships");
            }
            else
            {
                SetFinalize("Ran out of time");
                //Debug.Log($"Ending scouting command for {Squad.Name} because we ran out of time");
            }
        }


    }
}