using Assets.Scripts.Scenes;
using System.Collections;
using System.Security.Policy;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class ReconnectLevelRequest : SetupLevelRequest
    {

        public ReconnectLevelRequest(SetupLevel request, int maxTimeOnQueue, LevelStage level) : base(request, maxTimeOnQueue, level)
        {
            Type = "reconnect-level";
            Request = request;
            request.Type = Type;
            request.Hash = Hash;
        }
    }
}