using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreUserData
    {
        public readonly int UserId;
        public readonly string DataFile; // the name of the JSON chunk that will be fetched, equivilent to the file name if it was stored locally
        public readonly string Contents;
        public string Type;
        public long Hash;
        public StoreUserData(int userId, string dataFile, string contents)
        {
            this.UserId = userId;
            this.DataFile = dataFile;
            this.Contents = contents;
        }

    }
}