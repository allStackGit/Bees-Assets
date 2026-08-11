
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class ServerStoredCommand
    {
        public long Tsv, OutcomeId;
        public string Type;
        public long Hash;

        public ServerStoredCommand(long tsv, long outcomeId)
        {
            Tsv = tsv;
            OutcomeId = outcomeId;
        }
    }
}