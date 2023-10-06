using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreCommandsRequest : ServerRequest
    {
        public new StoreCommands Request = null;

        public StoreCommandsRequest(StoreCommands request, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "store-commands";
            Request = request;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}