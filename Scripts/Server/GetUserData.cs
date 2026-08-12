using System.Globalization;

namespace Assets.Scripts.Server
{
    public class GetUserData
    {
        public readonly string UserId;
        public readonly string DataFile; // the name of the JSON chunk that will be fetched, equivalent to the file name if it was stored locally
        public string AuthTicket;
        public string Type;
        public long Hash;

        public GetUserData(ulong userId, string dataFile)
        {
            UserId = userId.ToString(CultureInfo.InvariantCulture);
            DataFile = dataFile;
            AuthTicket = ConfigData.Production ? SteamWebApiAuth.TicketHex : null;
        }
    }
}
