using UnityEditor;
using UnityEngine;
namespace Assets.Scripts.Server
{
    public class ServerResponse
    {
        public ConfigData.RequestTypes RequestType;
        public string Type;
        public int Status;
        public int Hash;
       
        public ServerResponse(string type, int status, int hash)
        {
            Type = type;
            RequestType = Utilities.ConvertNameToRequestType[Type];
            Status = status;
            Hash = hash;
            Debug.Log($"{this}, {type}");
        }

        public override string ToString()
        {
            return $"SR #{Hash}:{RequestType} ({Status})";
        }
    }
}