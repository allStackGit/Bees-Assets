using Assets.Scripts.Settings;

namespace Assets.Scripts.Server
{
    public class GetUserSettingsData : GetUserData
    {
        public readonly float Version;
        public ServerSettings Settings;

        public GetUserSettingsData(ulong userId, string name, float version) : base(userId, name)
        {
            Version = version;
        }
    }
}
