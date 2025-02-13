using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class CommandResponse : ServerResponse
    {
        public string Name, ShootingStrategyName, MatchupString, ShootingStrategyMatchupString;
        public int StrategyId, ShootingStrategyId;
        public long SquadHash, MatchupId, OutcomeId, ShootingStrategyMatchupId, ShootingStrategyOutcomeId;
        public bool IsCached;
       
        public CommandResponse(string type, int status, int hash, string name, string shootingStrategyName, string matchupString, string shootingStrategyMatchupString, int strategyId, int shootingStrategyId,
            long matchupId, long shootingStrategyMatchupId, long outcomeId, long shootingStrategyOutcomeId, bool isCached) : base(type, status, hash)
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