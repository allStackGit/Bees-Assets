using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class GetMatchupStrategy
    {
        public string Ships;
        public ulong OpponentId;
        public string[] Banned;
        public string Type;
        public long Hash;

        public GetMatchupStrategy(string ships, ulong opponentId, string[] banned)
        {
            this.Ships = ships;
            this.OpponentId = opponentId;
            this.Banned = banned;
        }
    }
}