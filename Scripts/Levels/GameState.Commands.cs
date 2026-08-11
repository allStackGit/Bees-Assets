using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
        private readonly List<StoredCommand> _completes = new List<StoredCommand>();
        private readonly List<StoredCommand> _commands = new List<StoredCommand>();
        private readonly List<StoredCommand> _shootingCommands = new List<StoredCommand>();
        private readonly List<StoredCommand> _targetingCommands = new List<StoredCommand>();
        private List<Squad> _targetedSquads;

        public int AddUserCommand()
        {
            return UserCommands++;
        }

        public bool AddCommand(Command command)
        {
            if (command.OutcomeId > 0 && OutcomeIdToPastCommandIndex.ContainsKey(command.OutcomeId))
            {
                Debug.LogError($"Could not register duplicate command outcome #{command.OutcomeId}.");
                return false;
            }

            PastCommands.Add(new StoredCommand(command));
            if (command.OutcomeId > 0)
            {
                OutcomeIdToPastCommandIndex.Add(command.OutcomeId, PastCommands.Count - 1);
            }
            AICommands++;
            Stage.DebugLogger.__HivemindCommands++;
            return true;
        }

        private bool TryGetStoredCommand(long outcomeId, out StoredCommand storedCommand)
        {
            storedCommand = null;
            if (outcomeId <= 0 ||
                !OutcomeIdToPastCommandIndex.TryGetValue(outcomeId, out int storedCommandIndex) ||
                storedCommandIndex < 0 ||
                storedCommandIndex >= PastCommands.Count)
            {
                return false;
            }

            storedCommand = PastCommands[storedCommandIndex];
            return storedCommand != null && storedCommand.OutcomeId == outcomeId;
        }

        /// <summary>
        /// Adds delayed reward to the stored Hive Mind command that owns an outcome ID.
        /// Projectile damage can land after that command has finalized and a different
        /// command has started, while PastCommands remains alive until the level flush.
        /// </summary>
        public bool AddTsvToStoredCommand(long outcomeId, long tsvDelta)
        {
            if (!TryGetStoredCommand(outcomeId, out StoredCommand storedCommand))
            {
                return false;
            }

            storedCommand.Tsv += tsvDelta;
            return true;
        }

        /// <summary>
        /// Shooting policy learns from combat-only TSV. Strategic command TSV also includes
        /// spotting, mining, healing, and other command-specific reward that must not be
        /// attributed to target-priority selection.
        /// </summary>
        public bool AddShootingTsvToStoredCommand(long outcomeId, long tsvDelta)
        {
            if (!TryGetStoredCommand(outcomeId, out StoredCommand storedCommand))
            {
                return false;
            }

            storedCommand.ShootingTsv += tsvDelta;
            return true;
        }

        public void AddToSquadsAwaitingHiveMindCommands(Squad squad)
        {
            if (squad == null || squad.IsDead || SquadsAwaitingCommands.Contains(squad))
            {
                return;
            }
            SquadsAwaitingCommands.Enqueue(squad);
        }

        public Queue<Squad> GetSquadsAwaitingHiveMindCommands()
        {
            return SquadsAwaitingCommands;
        }

        public List<Squad> GetTargetedSquads(int side)
        {
            _targetedSquads = new List<Squad>();
            foreach (Squad squad in GetAllSquads().Where(squad => squad.Side == side))
            {
                if (squad.HasCommand && squad.GetCommand().HasEnemy && !squad.GetCommand().EnemySquad.IsDead)
                {
                    _targetedSquads.Add(squad.GetCommand().EnemySquad);
                }
            }
            return _targetedSquads;
        }

        public void StoreCommands()
        {
            _completes.Clear();
            _completes.AddRange(PastCommands.Where(command => command.IsHiveMindCommand && command.IsFinalized));
            if (_completes.Count == 0)
            {
                return;
            }

            foreach (StoredCommand command in _completes)
            {
                OutcomeIdToPastCommandIndex.Remove(command.OutcomeId);
                _commands.Add(command);

                if (command.ShootingStrategy == null)
                {
                    Debug.LogError("Stored command didn't have a shooting strategy");
                }
                else if (command.HasTargetingEnemy && command.CommandType != ConfigData.CommandTypes.Retreat)
                {
                    // A targetless shooting matchup has no enemy composition and cannot
                    // represent target-priority behavior. Retreat is also excluded because
                    // Socket currently executes Retreat with FirstSeen while retaining the
                    // server-selected shooting outcome ID. Until that response path is
                    // corrected, storing Retreat would train one strategy from another's behavior.
                    _shootingCommands.Add(command);
                }

                if (command.MatchupStrategy != null && command.HasTargetingEnemy)
                {
                    _targetingCommands.Add(command);
                }
            }

            ConfigData.Socket.SendRequest(new StoreCommandsRequest(
                new StoreCommands(_commands, _shootingCommands, _targetingCommands),
                ConfigData.StandardMaxTimeOnQueue));

            PastCommands.Clear();
            OutcomeIdToPastCommandIndex.Clear();
            _commands.Clear();
            _shootingCommands.Clear();
            _targetingCommands.Clear();
            _completes.Clear();
        }
    }
}
