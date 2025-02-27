
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
        public readonly long SquadId;
        public readonly Level Level;


        public MatchupStrategyRequest(GetMatchupStrategy request, Squad squad, Level level, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.GetMatchupStrategy;
            Request = request;
            Squad = squad;
            Level = level;
            Squad.Status = $"Requesting matchup strategy";
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
            SquadId = Squad.Id;
        }

        public bool HasSameSquad()
        {
            return Squad.Id == SquadId && !Squad.IsDead;
        }
    }
}