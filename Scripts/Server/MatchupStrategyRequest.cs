
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class MatchupStrategyRequest : ServerRequest
    {

        public new GetMatchupStrategy Request = null;
        public MatchupStrategyResponse Response = null;
        public readonly Squad Squad;


        public MatchupStrategyRequest(GetMatchupStrategy request, Squad squad, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "get-matchup-strategy";
            Request = request;
            Squad = squad;
            Squad.Status = $"Requesting matchup strategy";
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}