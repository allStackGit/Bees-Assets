using System.Collections.Generic;
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
        private readonly List<Squad> _targetedSquads = new List<Squad>();
        private readonly HashSet<Squad> _squadsAwaitingCommandSet = new HashSet<Squad>(ReferenceIdentityComparer<Squad>.Instance);

        public int AddUserCommand()
        {
            return UserCommands++;
        }

        private Squad GetPlayerCommandSquad(int squadItemId)
        {
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (squad != null && squad.ItemId == squadItemId)
                {
                    return squad;
                }
            }
            return null;
        }

        public bool CanPlayerCommandSquad(int playerId, Squad squad)
        {
            return IsKnownInputPlayer(playerId) &&
                squad != null &&
                !squad.IsDead &&
                ReferenceEquals(squad.Level, Level) &&
                Squads.Contains(squad) &&
                squad.CanAcceptInputFrom(playerId);
        }

        public bool TryPlayerMoveSquad(int playerId, int squadItemId, Vector2 destination)
        {
            Squad squad = GetPlayerCommandSquad(squadItemId);
            if (!CanPlayerCommandSquad(playerId, squad) || squad.IsLockedOn)
            {
                return false;
            }

            squad.FinalizeUserCommand();
            squad.Move(destination);
            return true;
        }

        public bool TryPlayerTargetEnemy(int playerId, int squadItemId, int enemySquadItemId)
        {
            Squad squad = GetPlayerCommandSquad(squadItemId);
            Squad enemySquad = GetPlayerCommandSquad(enemySquadItemId);
            if (!CanPlayerCommandSquad(playerId, squad) ||
                enemySquad == null ||
                enemySquad.IsDead ||
                enemySquad.Side == squad.Side)
            {
                return false;
            }

            squad.UserTargetEnemy(enemySquad);
            return true;
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

        public bool AddTsvToStoredCommand(long outcomeId, long tsvDelta)
        {
            if (!TryGetStoredCommand(outcomeId, out StoredCommand storedCommand))
            {
                return false;
            }

            storedCommand.Tsv += tsvDelta;
            return true;
        }

        public bool AddShootingTsvToStoredCommand(long outcomeId, long tsvDelta)
        {
            if (!TryGetStoredCommand(outcomeId, out StoredCommand storedCommand))
            {
                return false;
            }

            storedCommand.ShootingTsv += tsvDelta;
            return true;
        }

        private static bool CommandUsesSelectedEnemy(ConfigData.CommandTypes commandType)
        {
            switch (commandType)
            {
                case ConfigData.CommandTypes.Aggressive:
                case ConfigData.CommandTypes.BombingRun:
                case ConfigData.CommandTypes.Charge:
                case ConfigData.CommandTypes.Retreat:
                case ConfigData.CommandTypes.CircleSquad:
                case ConfigData.CommandTypes.RightSwipe:
                case ConfigData.CommandTypes.LeftSwipe:
                case ConfigData.CommandTypes.InAndOut:
                    return true;
                default:
                    return false;
            }
        }

        public void AddToSquadsAwaitingHiveMindCommands(Squad squad)
        {
            if (squad == null || squad.IsDead || !squad.IsHiveMindControlled ||
                !_squadsAwaitingCommandSet.Add(squad))
            {
                return;
            }
            SquadsAwaitingCommands.Enqueue(squad);
        }

        public bool TryDequeueSquadAwaitingHiveMindCommand(out Squad squad)
        {
            while (SquadsAwaitingCommands.Count > 0)
            {
                squad = SquadsAwaitingCommands.Dequeue();
                _squadsAwaitingCommandSet.Remove(squad);
                if (squad != null && !squad.IsDead && squad.IsHiveMindControlled)
                {
                    return true;
                }
            }

            squad = null;
            return false;
        }

        public void ClearSquadsAwaitingHiveMindCommands()
        {
            SquadsAwaitingCommands.Clear();
            _squadsAwaitingCommandSet.Clear();
        }

        public Queue<Squad> GetSquadsAwaitingHiveMindCommands()
        {
            return SquadsAwaitingCommands;
        }

        public List<Squad> GetTargetedSquads(int side)
        {
            _targetedSquads.Clear();
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (squad.Side != side || !squad.HasCommand)
                {
                    continue;
                }

                Command command = squad.GetCommand();
                if (command != null && command.HasEnemy && command.EnemySquad != null && !command.EnemySquad.IsDead)
                {
                    _targetedSquads.Add(command.EnemySquad);
                }
            }
            return _targetedSquads;
        }

        public void StoreCommands()
        {
            _completes.Clear();
            for (int i = 0; i < PastCommands.Count; i++)
            {
                StoredCommand command = PastCommands[i];
                if (command.IsHiveMindCommand && command.IsFinalized)
                {
                    _completes.Add(command);
                }
            }
            if (_completes.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _completes.Count; i++)
            {
                StoredCommand command = _completes[i];
                OutcomeIdToPastCommandIndex.Remove(command.OutcomeId);
                _commands.Add(command);

                if (command.ShootingStrategy == null)
                {
                    Debug.LogError("Stored command didn't have a shooting strategy");
                }
                else if (command.HasTargetingEnemy &&
                    command.CommandType != ConfigData.CommandTypes.Retreat &&
                    CommandUsesSelectedEnemy(command.CommandType))
                {
                    _shootingCommands.Add(command);
                }

                if (command.MatchupStrategy != null &&
                    command.HasTargetingEnemy &&
                    CommandUsesSelectedEnemy(command.CommandType))
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
