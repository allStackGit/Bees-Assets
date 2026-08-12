using System.Globalization;

namespace Assets.Scripts.Server
{
    public class SetupLevel
    {
        public int LevelId;
        /// <summary>
        /// The Id of the Game connection on the server
        /// </summary>
        public long GameId;
        // JSON numbers are parsed as IEEE-754 doubles by the Node server and cannot
        // exactly represent the full ulong/SteamID range. Keep the public constructor
        // strongly typed but put the identifier on the wire as decimal text; MySQL can
        // bind that string losslessly to its BIGINT userId columns.
        public string UserId;
        public string AuthTicket;
        public float Version;
        public string Type;
        public long Hash;

        public SetupLevel(int levelId, ulong userId, long gameId = 0)
        {
            LevelId = levelId;
            UserId = userId.ToString(CultureInfo.InvariantCulture);
            AuthTicket = ConfigData.Production ? SteamWebApiAuth.TicketHex : null;
            GameId = gameId;
        }
    }
}
