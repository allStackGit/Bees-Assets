
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
            commands.ForEach((storedCommand) =>
            {
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.OutcomeId));
            });
            Commands = temp.ToArray();
            temp.Clear();
            shootingCommands.ForEach((storedCommand) =>
            {
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.ShootingStrategy.OutcomeId));
            });
            ShootingCommands = temp.ToArray();
            temp.Clear();
            targetingCommands.ForEach((storedCommand) =>
            {
                temp.Add(new ServerStoredCommand(storedCommand.Tsv, storedCommand.MatchupStrategy.OutcomeId));
            });
            TargetingCommands = temp.ToArray();
            temp.Clear();
        }
    }
}