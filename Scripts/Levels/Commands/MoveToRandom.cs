
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class MoveToRandom : Command
    {
        private int _range;

        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup, int range = 0)
        {
            base.Setup(squad, isHiveMindCommand, enemy, matchup);
            _range = range > 0 ? range : ConfigData.Configuration.AIRandomMovementMaxDistance; 
        }
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true); 

            PrepareDamageToSendEntries(1);
            SetAndMove(Utilities.RandomCoordinate(Level, GetSquad().GetPosition(), Vector2.one * _range, Vector2.one * 32));
            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
            Level.AddTimer(CommandTimer);

            TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
            Level.AddTimer(TimeoutTimer);
        }
        private void Timer()
        {
            if (!IsDead)
            {
                if (GetSquad().HasReachedDestination)
                {
                    SetFinalize("Reached the random destination on the map");
                    return;
                }
                GetSquad().Status = $"Moving to random destination: {GetDestination()}";
            }
        }
    }
}