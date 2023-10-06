using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreUserDataRequest : ServerRequest
    {
        public new StoreUserData Request = null;

        public StoreUserDataRequest(StoreUserData request, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "store-user-data";
            Request = request;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}