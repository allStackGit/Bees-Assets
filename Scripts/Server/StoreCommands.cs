
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreCommands
    {
        public ServerStoredCommand[] Commands;
        public ServerStoredCommand[] ShootingCommands;
        public ServerStoredCommand[] TargetingCommands;
        public long[] DiscardedOutcomeIds;
        public string Type;
        public long Hash;

        public StoreCommands(List<StoredCommand> commands, List<StoredCommand> shootingCommands, List<StoredCommand> targetingCommands)
        {
            List<ServerStoredCommand> temp = new List<ServerStoredCommand>();
            HashSet<long> retainedSecondaryOutcomeIds = new HashSet<long>();
            HashSet<long> discardedOutcomeIds = new HashSet<long>();

            commands.ForEach((storedCommand) =>
            {
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.OutcomeId));
            });
            Commands = temp.ToArray();
            temp.Clear();

            shootingCommands.ForEach((storedCommand) =>
            {
                // Shooting target selection learns only from combat TSV accumulated by
                // Ship combat accounting, not from strategic reward such as spotting,
                // mining, healing, or retreat-specific value.
                long outcomeId = storedCommand.ShootingStrategy?.OutcomeId ?? 0;
                if (outcomeId > 0)
                {
                    retainedSecondaryOutcomeIds.Add(outcomeId);
                    temp.Add(new ServerStoredCommand(storedCommand.ShootingTsv, outcomeId));
                }
            });
            ShootingCommands = temp.ToArray();
            temp.Clear();

            targetingCommands.ForEach((storedCommand) =>
            {
                long outcomeId = storedCommand.MatchupStrategy?.OutcomeId ?? 0;
                if (outcomeId > 0)
                {
                    retainedSecondaryOutcomeIds.Add(outcomeId);
                    temp.Add(new ServerStoredCommand(storedCommand.Tsv, outcomeId));
                }
            });
            TargetingCommands = temp.ToArray();

            // The server reserves targeting/shooting outcome IDs when it selects policies.
            // Some selected secondary policies intentionally do not influence execution and
            // therefore must not receive reward. Explicitly release those reservations instead
            // of leaving them in the server's pending-insert map until its two-hour expiry.
            commands.ForEach((storedCommand) =>
            {
                long shootingOutcomeId = storedCommand.ShootingStrategy?.OutcomeId ?? 0;
                if (shootingOutcomeId > 0 && !retainedSecondaryOutcomeIds.Contains(shootingOutcomeId))
                {
                    discardedOutcomeIds.Add(shootingOutcomeId);
                }

                long targetingOutcomeId = storedCommand.MatchupStrategy?.OutcomeId ?? 0;
                if (targetingOutcomeId > 0 && !retainedSecondaryOutcomeIds.Contains(targetingOutcomeId))
                {
                    discardedOutcomeIds.Add(targetingOutcomeId);
                }
            });
            DiscardedOutcomeIds = new List<long>(discardedOutcomeIds).ToArray();
        }
    }
}
