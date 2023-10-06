
using System.Collections.Generic;
using UnityEditor;
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
            this.Tsv = tsv;
            this.OutcomeId = outcomeId;
        }
    }
}