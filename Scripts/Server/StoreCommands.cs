
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public class StoreCommands
    {
        public ServerStoredCommand[] Commands;
        public ServerStoredCommand[] ShootingCommands;
        public ServerStoredCommand[] TargetingCommands;
        public string Type;
        public long Hash;

        public StoreCommands(List<StoredCommand> commands, List<StoredCommand> shootingCommands, List<StoredCommand> targetingCommands)
        {
            List<ServerStoredCommand> temp = new List<ServerStoredCommand>();
            HashSet<long> ids = new HashSet<long>();
            commands.ForEach((storedCommand) =>
            {
                if (!ids.Contains(storedCommand.OutcomeId))
                {
                    ids.Add(storedCommand.OutcomeId);
                    Debug.Log($"Adding command #{storedCommand.OutcomeId}");
                }
                else
                {
                    Debug.LogError($"A command/strategy with OutcomeId #{storedCommand.OutcomeId} has already been added to the list");
                }
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.OutcomeId));
            });
            Commands = temp.ToArray();
            temp.Clear();
            shootingCommands.ForEach((storedCommand) =>
            {
                if (!ids.Contains(storedCommand.ShootingStrategy.OutcomeId))
                {
                    ids.Add(storedCommand.ShootingStrategy.OutcomeId);
                    Debug.Log($"Adding shooting strategy #{storedCommand.ShootingStrategy.OutcomeId}");
                }
                else
                {
                    Debug.LogError($"A command/strategy with OutcomeId #{storedCommand.ShootingStrategy.OutcomeId} has already been added to the list");
                }
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.ShootingStrategy.OutcomeId));
            });
            ShootingCommands = temp.ToArray();
            temp.Clear();
            targetingCommands.ForEach((storedCommand) =>
            {
                if (!ids.Contains(storedCommand.MatchupStrategy.OutcomeId))
                {
                    ids.Add(storedCommand.MatchupStrategy.OutcomeId);
                    Debug.Log($"Adding matchup strategy #{storedCommand.MatchupStrategy.OutcomeId}");
                }
                else
                {
                    Debug.LogError($"A command/strategy with OutcomeId #{storedCommand.MatchupStrategy.OutcomeId} has already been added to the list");
                }
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.MatchupStrategy.OutcomeId));
            });
            TargetingCommands = temp.ToArray();
        }
    }
}