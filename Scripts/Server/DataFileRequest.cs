using Assets.Scripts.Data;
using Assets.Scripts.Settings;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class DataFileRequest : ServerRequest
    {
        public new GetUserData Request = null;
        public UserDataResponse Response = null;
        public readonly DataFile DataFile;

        public DataFileRequest(GetUserData request, DataFile dataFile, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.GetUserData;
            Request = request;
            DataFile = dataFile;
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
        }
    }
}