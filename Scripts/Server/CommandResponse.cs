using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class CommandResponse : ServerResponse
    {
        public string Name, ShootingStrategyName, MatchupString, ShootingStrategyMatchupString;
        public string MatchupId, ShootingStrategyMatchupId;
        public int StrategyId, ShootingStrategyId;
        public long OutcomeId, ShootingStrategyOutcomeId;
        public bool IsCached;
       
        public CommandResponse(string type, int status, int hash, float serverLatency, string name, string shootingStrategyName, string matchupString, string shootingStrategyMatchupString, int strategyId, int shootingStrategyId,
            string matchupId, string shootingStrategyMatchupId, long outcomeId, long shootingStrategyOutcomeId, bool isCached) : base(type, status, hash, serverLatency)
        {
            Name = name;
            ShootingStrategyName = shootingStrategyName;
            MatchupString = matchupString;
            ShootingStrategyMatchupString = shootingStrategyMatchupString;
            StrategyId = strategyId;
            ShootingStrategyId = shootingStrategyId;
            MatchupId = matchupId;
            ShootingStrategyMatchupId = shootingStrategyMatchupId;
            OutcomeId = outcomeId;
            ShootingStrategyOutcomeId = shootingStrategyOutcomeId;
            IsCached = isCached;
        }

        public override string ToString()
        {
            return $"{Name}:#{OutcomeId} with SS:{ShootingStrategyName} and MS: {MatchupString}";
        }
    }
}