using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    /// <summary>
    /// Sends the squad to a specific point on the map. Only used for Override Commands
    /// </summary>
    public class MoveToPoint : Command
    {
        public Vector2 Destination;
        /// <summary>
        /// Used to set the variables for the command if it's not being created and executed in the same frame
        /// </summary>
        /// <param name="destination"></param>
        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup, Vector2 destination)
        {
            base.Setup(squad, isHiveMindCommand, enemy, matchup);
            Destination = destination;
        }
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            // MoveToPoint is an override-only command and intentionally bypasses base.Execute,
            // so it must establish active command ownership itself when execution begins.
            GetSquad().HasCommand = true;

            PrepareDamageToSendEntries(1);
            SetAndMove(Destination);
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
                    SetFinalize("Reached the specified destination on the map");
                    return;
                }
                if (!Stage.IsTraining)
                {
                    GetSquad().Status = $"Moving to specific destination: {Destination}";
                }
            }
        }
    }
}