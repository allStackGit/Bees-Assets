using UnityEditor;
using UnityEngine;
namespace Assets.Scripts.Server
{
    public class ServerResponse
    {
        public string Type;
        public int Status;
        public int Hash;
       
        public ServerResponse(string type, int status, int hash)
        {
            Type = type;
            Status = status;
            Hash = hash;
        }
    }
}