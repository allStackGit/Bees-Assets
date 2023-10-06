
using System.Collections;
using UnityEngine;
namespace Assets.Scripts.Server
{
    public class GetUserData
    {
        public readonly int UserId;
        public readonly string DataFile; // the name of the JSON chunk that will be fetched, equivilent to the file name if it was stored locally
        public string Type;
        public long Hash;
        public GetUserData(int userId, string dataFile)
        {
            this.UserId = userId;
            this.DataFile = dataFile;
        }
    }
}