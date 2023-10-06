using Assets.Scripts.Settings;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SettingsRequest : ServerRequest
    {

        public new GetUserSettingsData Request = null;
        public UserDataResponse Response = null;
        public readonly ServerSettings Settings;


        public SettingsRequest(GetUserSettingsData request, ServerSettings settings, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = "get-settings";
            Request = request;
            Settings = settings;
            request.Type = Type;
            request.Hash = Hash;
        }

        public void SetResponse(UserDataResponse response)
        {
            this.Response = response;
        }
    }
}