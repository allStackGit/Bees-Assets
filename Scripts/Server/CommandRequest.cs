

using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Linq;
using System.Security.Policy;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class CommandRequest : ServerRequest
    {
        public readonly Squad Squad, Enemy;
        public readonly Level Level;
        public readonly string Matchup;
        public readonly int SquadId;

        public new GetStrategy Request = null;
        public CommandResponse Response = null;
        public CommandRequest(GetStrategy request, Squad squad, Squad enemy, Level level, string matchup, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.GetStrategy;
            Request = request;
            Squad = squad;
            Enemy = enemy;
            Level = level;
            Matchup = matchup;
            Squad.Status = $"Requesting full command";
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
            SquadId = Squad.ItemId;
        }
        public bool HasSameSquad()
        {
            return SquadId == Squad.ItemId && !Squad.IsDead;
        }
    }
}