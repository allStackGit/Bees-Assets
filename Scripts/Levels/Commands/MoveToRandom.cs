
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class MoveToRandom : Command
    {
        /*
        Sends the squad towards a random spot on the map
         */
        private Vector2 _destination;
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true); 

            PrepareDamageToSendEntries("closest");
            SetAndMove(Utilities.RandomCoordinate(Level, GetSquad().GetPosition(), Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10));
            CommandTimer.Reuse(CommandFrequency, Timer, true);
            Level.AddTimer(CommandTimer);
            //InvokeRepeating(nameof(Timer), 0, CommandFrequency);


        }
        private void Timer()
        {
            if (!IsDead)
            {
                if (GetSquad().HasReachedDestination)
                {
                    //CancelInvoke(nameof(Timer));
                    SetFinalize("Reached the random destination on the map");
                }
                _destination = GetDestination();
                SetAndMove(_destination);
                GetSquad().Status = $"Moving to random destination: {_destination}";
            }
        }
    }
}