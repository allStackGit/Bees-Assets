using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{ 
    public class UserDataResponse : ServerResponse
    {
        public int UserId;
        public string Filename;
        public string Contents;

        public UserDataResponse(string type, int status, int userId, long hash, float serverLatency, string filename, string contents) : base(type, status, hash, serverLatency)
        {
            this.UserId = userId;
            this.Filename = filename;   
            this.Contents = contents;
        }
    }
}