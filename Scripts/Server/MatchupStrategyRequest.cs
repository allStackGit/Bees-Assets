
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class MatchupStrategyRequest : ServerRequest
    {

        public new GetMatchupStrategy Request = null;
        public MatchupStrategyResponse Response = null;
        public readonly Squad Squad;
        public readonly Level Level;


        public MatchupStrategyRequest(GetMatchupStrategy request, Squad squad, Level level, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "get-matchup-strategy";
            Request = request;
            Squad = squad;
            Level = level;
            Squad.Status = $"Requesting matchup strategy";
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}