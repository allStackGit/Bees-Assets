using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreCommandsRequest : ServerRequest
    {
        public new StoreCommands Request = null;

        public StoreCommandsRequest(StoreCommands request, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.StoreCommands;
            Request = request;
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
        }
    }
}