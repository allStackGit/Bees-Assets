using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private readonly Queue<(int SourcePeerId, PlayerCommandEnvelope Command)> _queuedPlayerCommands =
            new Queue<(int SourcePeerId, PlayerCommandEnvelope Command)>();
        private readonly object _queuedPlayerCommandsLock = new object();
        public const int MaxQueuedPlayerCommands = 1024;
        public const int MaxPlayerCommandsPerFrame = 64;

        public int AddUserCommand()
        {
            return UserCommands++;
        }

        public bool QueueReceivedPlayerCommandPacket(int sourcePeerId, byte[] payload)
        {
            if (!MultiplayerProtocol.TryDeserializeCommand(
                    payload,
                    MatchId,
                    out int matchLevelId,
                    out PlayerCommandEnvelope command) ||
                matchLevelId != MatchLevelId)
            {
                return false;
            }

            return QueueReceivedPlayerCommand(sourcePeerId, command);
        }

        public bool QueueReceivedPlayerCommand(int sourcePeerId, PlayerCommandEnvelope command)
        {
            if (sourcePeerId <= 0 || command == null || command.PlayerId <= MatchSession.UnownedPlayerId ||
                command.Sequence <= 0 || command.SquadCommandId <= 0)
            {
                return false;
            }

            PlayerCommandEnvelope queuedCopy = new PlayerCommandEnvelope(
                command.PlayerId,
                command.Sequence,
                command.Kind,
                command.SquadCommandId,
                command.TargetSquadCommandId,
                command.PointA,
                command.PointB);

            lock (_queuedPlayerCommandsLock)
            {
                if (_queuedPlayerCommands.Count >= MaxQueuedPlayerCommands)
                {
                    return false;
                }

                _queuedPlayerCommands.Enqueue((sourcePeerId, queuedCopy));
                return true;
            }
        }

        public int ProcessQueuedPlayerCommands(int maxCommands = MaxPlayerCommandsPerFrame)
        {
            if (maxCommands <= 0)
            {
                return 0;
            }
            if (GameOver || LevelEnded)
            {
                ClearQueuedPlayerCommands();
                return 0;
            }
            if (IsPaused)
            {
                return 0;
            }

            int processed = 0;
            while (processed < maxCommands)
            {
                (int SourcePeerId, PlayerCommandEnvelope Command) queuedCommand;
                lock (_queuedPlayerCommandsLock)
                {
                    if (_queuedPlayerCommands.Count == 0)
                    {
                        break;
                    }
                    queuedCommand = _queuedPlayerCommands.Dequeue();
                }

                TryExecuteReceivedPlayerCommand(queuedCommand.SourcePeerId, queuedCommand.Command);
                processed++;
            }
            return processed;
        }

        public void ClearQueuedPlayerCommands()
        {
            lock (_queuedPlayerCommandsLock)
            {
                _queuedPlayerCommands.Clear();
            }
        }

        public bool TryExecuteReceivedPlayerCommand(int sourcePeerId, PlayerCommandEnvelope command)
        {
            MatchSession matchSession = Stage != null ? Stage.MatchSession : null;
            if (matchSession == null || !matchSession.IsLocalAuthority || command == null ||
                !matchSession.DoesPeerOwnPlayer(sourcePeerId, command.PlayerId))
            {
                return false;
            }

            return TryExecutePlayerCommand(command);
        }

        public bool TryIssuePlayerCommand(
            int playerId,
            PlayerCommandKind kind,
            long squadCommandId,
            long targetSquadCommandId = 0,
            Vector2 pointA = default,
            Vector2 pointB = default)
        {
            MatchSession matchSession = Stage != null ? Stage.MatchSession : null;
            long sequence = 0;
            if (matchSession != null)
            {
                if (!matchSession.IsLocalPlayer(playerId))
                {
                    return false;
                }

                sequence = matchSession.AllocatePlayerCommandSequence(playerId);
                if (sequence <= 0)
                {
                    return false;
                }
            }
            else if (playerId != MatchSession.LegacyLocalPlayerId)
            {
                return false;
            }

            PlayerCommandEnvelope command = new PlayerCommandEnvelope(
                playerId,
                sequence,
                kind,
                squadCommandId,
                targetSquadCommandId,
                pointA,
                pointB);

            if (matchSession != null && !matchSession.IsLocalAuthority)
            {
                return matchSession.QueueOutgoingPlayerCommand(MatchLevelId, command);
            }

            return TryExecutePlayerCommand(command);
        }

        private bool TryExecutePlayerCommand(PlayerCommandEnvelope command)
        {
            if (command == null || !IsKnownInputPlayer(command.PlayerId))
            {
                return false;
            }

            MatchSession matchSession = Stage != null ? Stage.MatchSession : null;
            if (matchSession != null)
            {
                if (matchSession.Phase != MatchSessionPhase.Battle ||
                    !matchSession.TryAcceptPlayerCommandSequence(command.PlayerId, command.Sequence))
                {
                    return false;
                }
            }
            else if (command.Sequence > 0)
            {
                // Sequenced commands are a multiplayer transport contract and must not leak
                // into Campaign/Challenge's legacy no-session path.
                return false;
            }

            switch (command.Kind)
            {
                case PlayerCommandKind.Move:
                    return TryPlayerMoveSquad(command.PlayerId, command.SquadCommandId, command.PointA);
                case PlayerCommandKind.TargetEnemy:
                    return TryPlayerTargetEnemy(
                        command.PlayerId,
                        command.SquadCommandId,
                        command.TargetSquadCommandId);
                case PlayerCommandKind.Guard:
                    return TryPlayerGuardSquad(
                        command.PlayerId,
                        command.SquadCommandId,
                        command.TargetSquadCommandId);
                case PlayerCommandKind.Patrol:
                    return TryPlayerPatrolSquad(
                        command.PlayerId,
                        command.SquadCommandId,
                        command.PointA,
                        command.PointB);
                case PlayerCommandKind.FullRetreat:
                    return TryPlayerFullRetreat(
                        command.PlayerId,
                        command.SquadCommandId,
                        command.TargetSquadCommandId);
                case PlayerCommandKind.Heal:
                    return TryPlayerHealSquad(
                        command.PlayerId,
                        command.SquadCommandId,
                        command.TargetSquadCommandId);
                default:
                    return false;
            }
        }

        private Squad GetPlayerCommandSquad(long squadCommandId)
        {
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (squad != null && squad.CommandSquadId == squadCommandId)
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

        public bool TryPlayerMoveSquad(int playerId, long squadCommandId, Vector2 destination)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            if (!CanPlayerCommandSquad(playerId, squad) || squad.IsLockedOn)
            {
                return false;
            }

            squad.FinalizeUserCommand();
            squad.Move(destination);
            return true;
        }

        public bool TryPlayerTargetEnemy(int playerId, long squadCommandId, long enemySquadCommandId)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            Squad enemySquad = GetPlayerCommandSquad(enemySquadCommandId);
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

        public bool TryPlayerGuardSquad(int playerId, long squadCommandId, long friendlySquadCommandId)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            Squad friendlySquad = GetPlayerCommandSquad(friendlySquadCommandId);
            if (!CanPlayerCommandSquad(playerId, squad) ||
                friendlySquad == null ||
                friendlySquad.IsDead ||
                friendlySquad.Side != squad.Side ||
                ReferenceEquals(friendlySquad, squad))
            {
                return false;
            }

            squad.UserGuard(friendlySquad);
            return true;
        }

        public bool TryPlayerPatrolSquad(int playerId, long squadCommandId, Vector2 topLeft, Vector2 bottomRight)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            if (!CanPlayerCommandSquad(playerId, squad))
            {
                return false;
            }

            squad.UserPatrol(topLeft, bottomRight);
            return true;
        }

        public bool TryPlayerFullRetreat(int playerId, long squadCommandId, long warpGateSquadCommandId)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            Squad warpGateSquad = GetPlayerCommandSquad(warpGateSquadCommandId);
            if (!CanPlayerCommandSquad(playerId, squad) ||
                warpGateSquad == null ||
                warpGateSquad.IsDead ||
                warpGateSquad.Side != squad.Side)
            {
                return false;
            }

            List<Ship> targetShips = warpGateSquad.GetShips();
            for (int i = 0; i < targetShips.Count; i++)
            {
                if (targetShips[i] is WarpGate warpGate && !warpGate.IsDead)
                {
                    squad.UserFullRetreat(warpGate);
                    return true;
                }
            }
            return false;
        }

        public bool TryPlayerHealSquad(int playerId, long squadCommandId, long beehiveSquadCommandId)
        {
            Squad squad = GetPlayerCommandSquad(squadCommandId);
            Squad beehiveSquad = GetPlayerCommandSquad(beehiveSquadCommandId);
            if (!CanPlayerCommandSquad(playerId, squad) ||
                beehiveSquad == null ||
                beehiveSquad.IsDead ||
                beehiveSquad.Side != squad.Side)
            {
                return false;
            }

            List<Beehive> beehives = new List<Beehive>();
            List<Ship> targetShips = beehiveSquad.GetShips();
            for (int i = 0; i < targetShips.Count; i++)
            {
                if (targetShips[i] is Beehive beehive && !beehive.IsDead)
                {
                    beehives.Add(beehive);
                }
            }
            if (beehives.Count == 0)
            {
                return false;
            }

            squad.UserHeal(beehives);
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

    public enum PlayerCommandKind
    {
        Move,
        TargetEnemy,
        Guard,
        Patrol,
        FullRetreat,
        Heal
    }

    /// <summary>
    /// Transport-neutral player order. A network/local transport may serialize this object, but
    /// execution remains authoritative in GameState and is revalidated against current ownership.
    /// </summary>
    [Serializable]
    public sealed class PlayerCommandEnvelope
    {
        public int PlayerId;
        public long Sequence;
        public PlayerCommandKind Kind;
        public long SquadCommandId;
        public long TargetSquadCommandId;
        public Vector2 PointA;
        public Vector2 PointB;

        public PlayerCommandEnvelope()
        {
        }

        public PlayerCommandEnvelope(
            int playerId,
            long sequence,
            PlayerCommandKind kind,
            long squadCommandId,
            long targetSquadCommandId = 0,
            Vector2 pointA = default,
            Vector2 pointB = default)
        {
            PlayerId = playerId;
            Sequence = sequence;
            Kind = kind;
            SquadCommandId = squadCommandId;
            TargetSquadCommandId = targetSquadCommandId;
            PointA = pointA;
            PointB = pointB;
        }
    }


    public static class MultiplayerProtocol
    {
        public const int Version = 1;
        public const int MaxPacketBytes = 4096;
        private const string CommandPacketType = "command";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> CommandFields = new HashSet<string>
        {
            "v", "match", "type", "level", "player", "seq", "kind", "squad", "target",
            "ax", "ay", "bx", "by"
        };

        public static bool TrySerializeCommand(
            Guid matchId,
            int matchLevelId,
            PlayerCommandEnvelope command,
            out byte[] payload)
        {
            payload = null;
            if (matchId == Guid.Empty || matchLevelId <= 0 || !IsValidCommand(command))
            {
                return false;
            }

            JObject json = new JObject
            {
                ["v"] = Version,
                ["match"] = matchId.ToString("N"),
                ["type"] = CommandPacketType,
                ["level"] = matchLevelId,
                ["player"] = command.PlayerId,
                ["seq"] = command.Sequence,
                ["kind"] = (int)command.Kind,
                ["squad"] = command.SquadCommandId,
                ["target"] = command.TargetSquadCommandId,
                ["ax"] = command.PointA.x,
                ["ay"] = command.PointA.y,
                ["bx"] = command.PointB.x,
                ["by"] = command.PointB.y
            };

            byte[] encoded = StrictUtf8.GetBytes(json.ToString(Formatting.None));
            if (encoded.Length == 0 || encoded.Length > MaxPacketBytes)
            {
                return false;
            }

            payload = encoded;
            return true;
        }

        public static bool TryDeserializeCommand(
            byte[] payload,
            Guid expectedMatchId,
            out int matchLevelId,
            out PlayerCommandEnvelope command)
        {
            matchLevelId = 0;
            command = null;
            if (expectedMatchId == Guid.Empty ||
                payload == null ||
                payload.Length == 0 ||
                payload.Length > MaxPacketBytes)
            {
                return false;
            }

            JObject json;
            try
            {
                string text = StrictUtf8.GetString(payload);
                json = JObject.Parse(text);
            }
            catch (Exception)
            {
                return false;
            }

            foreach (JProperty property in json.Properties())
            {
                if (!CommandFields.Contains(property.Name))
                {
                    return false;
                }
            }
            if (json.Count != CommandFields.Count)
            {
                return false;
            }

            if (!TryReadInt64(json, "v", out long version) || version != Version ||
                !TryReadString(json, "type", out string packetType) || packetType != CommandPacketType ||
                !TryReadString(json, "match", out string matchText) ||
                !Guid.TryParseExact(matchText, "N", out Guid matchId) ||
                matchId != expectedMatchId ||
                !TryReadInt64(json, "level", out long matchLevelIdValue) ||
                matchLevelIdValue <= 0 || matchLevelIdValue > int.MaxValue ||
                !TryReadInt64(json, "player", out long playerIdValue) ||
                playerIdValue <= MatchSession.UnownedPlayerId ||
                playerIdValue > int.MaxValue ||
                !TryReadInt64(json, "seq", out long sequence) || sequence <= 0 ||
                !TryReadInt64(json, "kind", out long kindValue) ||
                kindValue < int.MinValue || kindValue > int.MaxValue ||
                !Enum.IsDefined(typeof(PlayerCommandKind), (int)kindValue) ||
                !TryReadInt64(json, "squad", out long squadCommandId) || squadCommandId <= 0 ||
                !TryReadInt64(json, "target", out long targetSquadCommandId) || targetSquadCommandId < 0 ||
                !TryReadFloat(json, "ax", out float ax) ||
                !TryReadFloat(json, "ay", out float ay) ||
                !TryReadFloat(json, "bx", out float bx) ||
                !TryReadFloat(json, "by", out float by))
            {
                return false;
            }

            PlayerCommandEnvelope parsed = new PlayerCommandEnvelope(
                (int)playerIdValue,
                sequence,
                (PlayerCommandKind)(int)kindValue,
                squadCommandId,
                targetSquadCommandId,
                new Vector2(ax, ay),
                new Vector2(bx, by));

            if (!IsValidCommand(parsed))
            {
                return false;
            }

            matchLevelId = (int)matchLevelIdValue;
            command = parsed;
            return true;
        }

        private static bool IsValidCommand(PlayerCommandEnvelope command)
        {
            if (command == null ||
                command.PlayerId <= MatchSession.UnownedPlayerId ||
                command.Sequence <= 0 ||
                command.SquadCommandId <= 0 ||
                command.TargetSquadCommandId < 0 ||
                !Enum.IsDefined(typeof(PlayerCommandKind), command.Kind) ||
                !IsFinite(command.PointA) ||
                !IsFinite(command.PointB))
            {
                return false;
            }

            bool requiresTarget =
                command.Kind == PlayerCommandKind.TargetEnemy ||
                command.Kind == PlayerCommandKind.Guard ||
                command.Kind == PlayerCommandKind.FullRetreat ||
                command.Kind == PlayerCommandKind.Heal;
            if (requiresTarget != (command.TargetSquadCommandId > 0))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadInt64(JObject json, string name, out long value)
        {
            value = 0;
            JToken token = json[name];
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            try
            {
                value = token.Value<long>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryReadFloat(JObject json, string name, out float value)
        {
            value = 0;
            JToken token = json[name];
            if (token == null ||
                (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                return false;
            }

            try
            {
                double parsed = token.Value<double>();
                if (double.IsNaN(parsed) || double.IsInfinity(parsed) ||
                    parsed > float.MaxValue || parsed < -float.MaxValue)
                {
                    return false;
                }

                value = (float)parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryReadString(JObject json, string name, out string value)
        {
            value = null;
            JToken token = json[name];
            if (token == null || token.Type != JTokenType.String)
            {
                return false;
            }

            value = token.Value<string>();
            return value != null;
        }

        private static bool IsFinite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y);
        }
    }


    public interface IMultiplayerTransport : IDisposable
    {
        bool IsAvailable { get; }
        void Update();
    }

}
