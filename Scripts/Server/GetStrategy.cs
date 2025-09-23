
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
            BannedStrats = bannedStrats;

        }
    }
}