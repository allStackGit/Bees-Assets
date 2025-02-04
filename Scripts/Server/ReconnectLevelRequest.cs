using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;

namespace Assets.Scripts.Server
{
    public class ReconnectLevelRequest : SetupLevelRequest
    {

        public ReconnectLevelRequest(SetupLevel request, int maxTimeOnQueue, Level level) : base(request, maxTimeOnQueue, level)
        {
            Type = ConfigData.RequestTypes.ReconnectLevel;
            Request = request;
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
        }
    }
}