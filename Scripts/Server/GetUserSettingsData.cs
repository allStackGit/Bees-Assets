using Assets.Scripts.Settings;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class GetUserSettingsData : GetUserData
    {
        public readonly float Version;
        public ServerSettings Settings;
        public GetUserSettingsData(int userId, string name, float version) : base(userId, name)
        {
            Version = version;
        }
    }
}