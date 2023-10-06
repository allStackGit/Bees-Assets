using UnityEditor;
using UnityEngine;
namespace Assets.Scripts.Server
{
    public class ServerResponse
    {
        public string Type;
        public int Status;
        public long Hash;
       
        public ServerResponse(string type, int status, long hash)
        {
            Type = type;
            Status = status;
            Hash = hash;
        }
    }
}