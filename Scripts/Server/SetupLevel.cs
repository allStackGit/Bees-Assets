using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevel
    {
        public int LevelId;
        /// <summary>
        /// The Id of the Game connection on the server
        /// </summary>
        public long GameId;
        public int UserId;
        public float Version;
        public string Type;
        public long Hash;

        public SetupLevel(int levelId, int userId, float version, long gameId = 0)
        {
            this.LevelId = levelId;    
            this.UserId = userId;
            this.Version = version;
            this.GameId = gameId;
        }
    }
}