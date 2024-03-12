using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevelRequest : ServerRequest
    {

        public new SetupLevel Request = null;
        public readonly LevelStage Level;


        public SetupLevelRequest(SetupLevel request, int maxTimeOnQueue, LevelStage level) : base(maxTimeOnQueue)
        {
            Type = "setup-level";
            Request = request;
            Level = level;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}