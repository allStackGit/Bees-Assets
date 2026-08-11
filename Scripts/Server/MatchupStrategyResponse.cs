using UnityEngine;

namespace Assets.Scripts.Server
{
    public class MatchupStrategyResponse : ServerResponse
    {
        public string Name, MatchupString, MatchupId;
        public int StrategyId;
        public long SquadHash, OutcomeId, HistoricalTsv, HistoricalUses, Rng, WeightedTsv;
       
        public MatchupStrategyResponse(string type, int status, int hash, float serverLatency, string name, string matchupString, int strategyId, long squadHash, 
            string matchupId, long outcomeId, long historicalTsv, long historicalUses, long rng, long weightedTsv) : base(type, status, hash, serverLatency)
        {
            this.Name = name;
            this.MatchupString = matchupString;
            this.StrategyId = strategyId;
            this.SquadHash = squadHash;
            this.MatchupId = matchupId;
            this.OutcomeId = outcomeId;
            this.HistoricalTsv = historicalTsv;
            this.HistoricalUses = historicalUses;
            this.Rng = rng;
            this.WeightedTsv = weightedTsv;
        }
    }
}