using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{ 
    public class UserDataResponse : ServerResponse
    {
        public int UserId;
        public string Filename;
        public string Contents;

        public UserDataResponse(string type, int status, int userId, int hash, string filename, string contents) : base(type, status, hash)
        {
            this.UserId = userId;
            this.Filename = filename;   
            this.Contents = contents;
        }
    }
}