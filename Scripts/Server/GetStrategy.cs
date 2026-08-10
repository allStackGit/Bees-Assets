
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class GetStrategy
    {
        public readonly string Matchup;
        public readonly int Side;
        public readonly ulong OpponentId;
        public readonly string[] BannedStrats;
        public string Type;
        public long Hash;
        public GetStrategy(string matchup, ulong opponentId, string[] bannedStrats)
        {
            Matchup = matchup;
            OpponentId = opponentId;
            // Move To Point requires a destination that GetStrategy responses do not carry.
            // Keep it out of server-authored command selection even though it is a valid
            // local/scripted command type.
            BannedStrats = (bannedStrats ?? new string[0])
                .Concat(new[] { "Move To Point" })
                .Distinct()
                .ToArray();

        }
    }
}