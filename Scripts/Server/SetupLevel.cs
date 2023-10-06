using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevel
    {
        public int LevelId;
        public int UserId;
        public float Version;
        public string Type;
        public long Hash;

        public SetupLevel(int levelId, int userId, float version)
        {
            this.LevelId = levelId;    
            this.UserId = userId;
            this.Version = version;
        }
    }
}