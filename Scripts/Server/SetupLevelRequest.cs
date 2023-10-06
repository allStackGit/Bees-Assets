using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevelRequest : ServerRequest
    {

        public new SetupLevel Request = null;


        public SetupLevelRequest(SetupLevel request, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "setup-level";
            Request = request;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}