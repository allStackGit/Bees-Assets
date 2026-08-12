using Assets.Scripts.Settings;

namespace Assets.Scripts.Server
{
    public class GetUserSettingsData : GetUserData
    {
        public readonly float Version;
        public readonly string AuthTicket;
        public ServerSettings Settings;

        public GetUserSettingsData(ulong userId, string name, float version) : base(userId, name)
        {
            Version = version;
            AuthTicket = ConfigData.Production ? SteamWebApiAuth.TicketHex : null;
        }
    }
}
