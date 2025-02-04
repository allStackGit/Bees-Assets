using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreUserDataRequest : ServerRequest
    {
        public new StoreUserData Request = null;

        public StoreUserDataRequest(StoreUserData request, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.StoreUserData;
            Request = request;
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
        }
    }
}