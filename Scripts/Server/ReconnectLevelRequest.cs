using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;

namespace Assets.Scripts.Server
{
    public class ReconnectLevelRequest : SetupLevelRequest
    {

        public ReconnectLevelRequest(SetupLevel request, int maxTimeOnQueue, Level level) : base(request, maxTimeOnQueue, level)
        {
            Type = "reconnect-level";
            Request = request;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}