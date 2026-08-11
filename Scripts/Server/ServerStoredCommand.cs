
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class ServerStoredCommand
    {
        public long Tsv, OutcomeId;
        public bool DiscardReservation;
        public string Type;
        public long Hash;

        public ServerStoredCommand(long tsv, long outcomeId, bool discardReservation = false)
        {
            Tsv = tsv;
            OutcomeId = outcomeId;
            DiscardReservation = discardReservation;
        }
    }
}
