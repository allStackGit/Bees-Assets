using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Hold : Command
    {
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

            PrepareDamageToSendEntries();
            TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
            Level.AddTimer(TimeoutTimer);
            GetSquad().Status = $"Holding this position";
        }
    }
}