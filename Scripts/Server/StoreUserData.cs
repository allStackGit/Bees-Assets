using System.Globalization;

namespace Assets.Scripts.Server
{
    public class StoreUserData
    {
        public readonly string UserId;
        public readonly string DataFile; // the name of the JSON chunk that will be fetched, equivalent to the file name if it was stored locally
        public readonly string Contents;
        public readonly string AuthTicket;
        public string Type;
        public long Hash;

        public StoreUserData(ulong userId, string dataFile, string contents)
        {
            UserId = userId.ToString(CultureInfo.InvariantCulture);
            DataFile = dataFile;
            Contents = contents;
            AuthTicket = SteamWebApiAuth.TicketHex;
        }
    }
}