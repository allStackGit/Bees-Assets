using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevelRequest : ServerRequest
    {

        public new SetupLevel Request = null;
        public readonly Level Level;


        public SetupLevelRequest(SetupLevel request, int maxTimeOnQueue, Level level) : base(maxTimeOnQueue)
        {
            Type = "setup-level";
            Request = request;
            Level = level;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}