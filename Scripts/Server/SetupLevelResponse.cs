using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class SetupLevelResponse : ServerResponse
    {
        /// <summary>
        /// The Id of the Game connection on the server
        /// </summary>
        public long GameId;

        public SetupLevelResponse(string type, int status, int hash, float serverLatency, long gameId) : base(type, status, hash, serverLatency)
        {
            this.GameId = gameId;
        }
    }
}
