

using Assets.Scripts.Level;
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
        public readonly string Matchup;

        public new GetStrategy Request = null;
        public CommandResponse Response = null;
        public CommandRequest(GetStrategy request, Squad squad, Squad enemy, string matchup, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "get-strategy";
            Request = request;
            Squad = squad;
            Enemy = enemy;
            Matchup = matchup;
            Squad.Status = $"Requesting full command";
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}