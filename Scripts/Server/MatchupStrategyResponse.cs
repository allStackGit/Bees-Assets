using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class MatchupStrategyResponse : ServerResponse
    {
        public string  Name, MatchupString;
        public int StrategyId;
        public long SquadHash, MatchupId, OutcomeId, HistoricalTsv, HistoricalUses, Rng, WeightedTsv;
       
        public MatchupStrategyResponse(string type, int status, long hash, string name, string matchupString, int strategyId, long squadHash, 
            long matchupId, long outcomeId, long historicalTsv, long historicalUses, long rng, long weightedTsv) : base(type, status, hash)
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