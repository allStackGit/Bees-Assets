namespace Assets.Scripts.Server
{
    public class UserDataResponse : ServerResponse
    {
        public string UserId;
        public string Filename;
        public string Contents;

        public UserDataResponse(string type, int status, string userId, long hash, float serverLatency, string filename, string contents)
            : base(type, status, hash, serverLatency)
        {
            UserId = userId;
            Filename = filename;
            Contents = contents;
        }
    }
}