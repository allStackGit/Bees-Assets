using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class CommandResponse : ServerResponse
    {
        public string Name, ShootingStrategyName, MatchupString, ShootingStrategyMatchupString;
        public int StrategyId, ShootingStrategyId;
        public long SquadHash, MatchupId, OutcomeId, ShootingStrategyMatchupId, ShootingStrategyOutcomeId;
       
        public CommandResponse(string type, int status, int hash, string name, string shootingStrategyName, string matchupString, string shootingStrategyMatchupString, int strategyId, int shootingStrategyId,
            long matchupId, long shootingStrategyMatchupId, long outcomeId, long shootingStrategyOutcomeId) : base(type, status, hash)
        {
            this.Name = name;
            this.ShootingStrategyName = shootingStrategyName;
            this.MatchupString = matchupString;
            this.ShootingStrategyMatchupString = shootingStrategyMatchupString;
            this.StrategyId = strategyId;
            this.ShootingStrategyId = shootingStrategyId;
            this.MatchupId = matchupId;
            this.ShootingStrategyMatchupId = shootingStrategyMatchupId;
            this.OutcomeId = outcomeId;
            this.ShootingStrategyOutcomeId = shootingStrategyOutcomeId;
        }
    }
}