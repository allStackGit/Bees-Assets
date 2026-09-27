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
            MatchSession matchSession = Stage != null ? Stage.MatchSession : null;
            if (matchSession == null ||
                !matchSession.IsLocalAuthority ||
                sourcePeerId <= 0 ||
                command == null ||
                command.PlayerId <= MatchSession.UnownedPlayerId ||
                command.Sequence <= 0 ||
                command.SquadCommandId <= 0 ||
                !matchSession.DoesPeerOwnPlayer(sourcePeerId, command.PlayerId))
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

            long lastAccepted = matchSession.GetLastAcceptedPlayerCommandSequence(command.PlayerId);
            if (command.Sequence <= lastAccepted)
            {
                matchSession.QueuePlayerCommandAcknowledgement(
                    sourcePeerId,
                    command.PlayerId,
                    lastAccepted);
                return true;
            }
            if (command.Sequence != lastAccepted + 1)
            {
                return false;
            }

            bool executed = TryExecutePlayerCommand(command);
            if (matchSession.GetLastAcceptedPlayerCommandSequence(command.PlayerId) >= command.Sequence)
            {
                matchSession.QueuePlayerCommandAcknowledgement(
                    sourcePeerId,
                    command.PlayerId,
                    command.Sequence);
            }
            return executed;
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
        public const int MaxLobbyPacketBytes = 65536;
        private const string CommandPacketType = "command";
        private const string AcknowledgementPacketType = "ack";
        private const string LobbyPacketType = "lobby";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> CommandFields = new HashSet<string>
        {
            "v", "match", "type", "level", "player", "seq", "kind", "squad", "target",
            "ax", "ay", "bx", "by"
        };
        private static readonly HashSet<string> AcknowledgementFields = new HashSet<string>
        {
            "v", "match", "type", "player", "seq"
        };
        private static readonly HashSet<string> LobbyFields = new HashSet<string>
        {
            "v", "type", "match", "seed", "authority", "peers", "players",
            "beeTypes", "humanTypes", "level", "squads"
        };
        private static readonly HashSet<string> LobbyPeerFields = new HashSet<string>
        {
            "id", "identity"
        };
        private static readonly HashSet<string> LobbyPlayerFields = new HashSet<string>
        {
            "id", "peer", "side"
        };
        private static readonly HashSet<string> LobbySquadFields = new HashSet<string>
        {
            "role", "token", "owner", "id", "side", "name", "sx", "sy",
            "r", "g", "b", "a", "cease", "matching", "chase", "strategy", "ships"
        };
        private static readonly HashSet<string> LobbyShipFields = new HashSet<string>
        {
            "id", "type", "name", "ox", "oy"
        };
        private static readonly HashSet<string> LobbyLevelFields = new HashSet<string>
        {
            "id", "side", "name", "map", "obstacles", "asteroids", "fog", "mining",
            "intro", "actionBox", "supply", "reinforceOption", "reinforceDelay",
            "enemyType", "enemyCount", "report", "beeX", "beeY", "humanX", "humanY",
            "obstacleList"
        };
        private static readonly HashSet<string> LobbyObstacleFields = new HashSet<string>
        {
            "px", "py", "sx", "sy"
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

        public static bool TrySerializeLobbySnapshot(
            MatchLobbySnapshot snapshot,
            out byte[] payload)
        {
            payload = null;
            if (!MatchSession.IsValidLobbySnapshot(snapshot))
            {
                return false;
            }

            JArray peers = new JArray();
            for (int i = 0; i < snapshot.Peers.Count; i++)
            {
                MatchLobbyPeerSnapshot peer = snapshot.Peers[i];
                peers.Add(new JObject
                {
                    ["id"] = peer.PeerId,
                    ["identity"] = peer.TransportIdentity
                });
            }

            JArray players = new JArray();
            for (int i = 0; i < snapshot.Players.Count; i++)
            {
                MatchLobbyPlayerSnapshot player = snapshot.Players[i];
                players.Add(new JObject
                {
                    ["id"] = player.PlayerId,
                    ["peer"] = player.PeerId,
                    ["side"] = player.Side
                });
            }

            JArray beeTypes = new JArray(snapshot.BeeRandomShipTypes);
            JArray humanTypes = new JArray(snapshot.HumanRandomShipTypes);

            JArray squads = new JArray();
            for (int i = 0; i < snapshot.Squads.Count; i++)
            {
                MatchLobbySquadSnapshot squad = snapshot.Squads[i];
                JArray ships = new JArray();
                for (int shipIndex = 0; shipIndex < squad.Ships.Count; shipIndex++)
                {
                    MatchLobbyShipSnapshot ship = squad.Ships[shipIndex];
                    ships.Add(new JObject
                    {
                        ["id"] = ship.TransientFleetId,
                        ["type"] = ship.ShipType,
                        ["name"] = ship.Name,
                        ["ox"] = ship.OffsetX,
                        ["oy"] = ship.OffsetY
                    });
                }

                squads.Add(new JObject
                {
                    ["role"] = squad.Role,
                    ["token"] = squad.OwnershipToken,
                    ["owner"] = squad.OwnerPlayerId,
                    ["id"] = squad.TransientSquadId,
                    ["side"] = squad.Side,
                    ["name"] = squad.Name,
                    ["sx"] = squad.StartingX,
                    ["sy"] = squad.StartingY,
                    ["r"] = squad.ColorR,
                    ["g"] = squad.ColorG,
                    ["b"] = squad.ColorB,
                    ["a"] = squad.ColorA,
                    ["cease"] = squad.CeaseFire,
                    ["matching"] = squad.IsMatchingSpeed,
                    ["chase"] = squad.IsSetToChase,
                    ["strategy"] = squad.ShootingStrategy,
                    ["ships"] = ships
                });
            }

            JToken level = JValue.CreateNull();
            if (snapshot.Level != null)
            {
                JArray obstacleList = new JArray();
                for (int i = 0; i < snapshot.Level.ObstacleList.Count; i++)
                {
                    MatchLobbyObstacleSnapshot obstacle = snapshot.Level.ObstacleList[i];
                    obstacleList.Add(new JObject
                    {
                        ["px"] = obstacle.PositionX,
                        ["py"] = obstacle.PositionY,
                        ["sx"] = obstacle.ScaleX,
                        ["sy"] = obstacle.ScaleY
                    });
                }

                level = new JObject
                {
                    ["id"] = snapshot.Level.Id,
                    ["side"] = snapshot.Level.Side,
                    ["name"] = snapshot.Level.Name,
                    ["map"] = snapshot.Level.MapIndex,
                    ["obstacles"] = snapshot.Level.Obstacles,
                    ["asteroids"] = snapshot.Level.AsteroidOption,
                    ["fog"] = snapshot.Level.FogOfWar,
                    ["mining"] = snapshot.Level.Mining,
                    ["intro"] = snapshot.Level.HasPreLevelIntro,
                    ["actionBox"] = snapshot.Level.HasSquadActionBox,
                    ["supply"] = snapshot.Level.SupplyCapacity,
                    ["reinforceOption"] = snapshot.Level.EnemyReinforcementsOption,
                    ["reinforceDelay"] = snapshot.Level.EnemyReinforcementDelay,
                    ["enemyType"] = snapshot.Level.EnemyShipTypeOption,
                    ["enemyCount"] = snapshot.Level.EnemySquadGenerationCount,
                    ["report"] = snapshot.Level.EnemyReport,
                    ["beeX"] = snapshot.Level.BeeStartingX,
                    ["beeY"] = snapshot.Level.BeeStartingY,
                    ["humanX"] = snapshot.Level.HumanStartingX,
                    ["humanY"] = snapshot.Level.HumanStartingY,
                    ["obstacleList"] = obstacleList
                };
            }

            JObject json = new JObject
            {
                ["v"] = Version,
                ["type"] = LobbyPacketType,
                ["match"] = snapshot.MatchId,
                ["seed"] = snapshot.SetupSeed,
                ["authority"] = snapshot.AuthorityPeerId,
                ["peers"] = peers,
                ["players"] = players,
                ["beeTypes"] = beeTypes,
                ["humanTypes"] = humanTypes,
                ["level"] = level,
                ["squads"] = squads
            };

            byte[] encoded = StrictUtf8.GetBytes(json.ToString(Formatting.None));
            if (encoded.Length == 0 || encoded.Length > MaxLobbyPacketBytes)
            {
                return false;
            }

            payload = encoded;
            return true;
        }

        public static bool TryDeserializeLobbySnapshot(
            byte[] payload,
            out MatchLobbySnapshot snapshot)
        {
            snapshot = null;
            if (payload == null ||
                payload.Length == 0 ||
                payload.Length > MaxLobbyPacketBytes)
            {
                return false;
            }

            JObject json;
            try
            {
                json = JObject.Parse(StrictUtf8.GetString(payload));
            }
            catch (Exception)
            {
                return false;
            }

            if (!HasExactFields(json, LobbyFields) ||
                !TryReadInt64(json, "v", out long version) ||
                version != Version ||
                !TryReadString(json, "type", out string packetType) ||
                packetType != LobbyPacketType ||
                !TryReadString(json, "match", out string matchId) ||
                !TryReadInt64(json, "seed", out long setupSeed) ||
                setupSeed <= 0 ||
                setupSeed > int.MaxValue ||
                !TryReadInt64(json, "authority", out long authorityPeerId) ||
                authorityPeerId <= 0 ||
                authorityPeerId > int.MaxValue ||
                !(json["peers"] is JArray peers) ||
                peers.Count == 0 ||
                peers.Count > MatchSession.MaxLobbyPeers ||
                !(json["players"] is JArray players) ||
                players.Count == 0 ||
                players.Count > MatchSession.MaxLobbyPlayers ||
                !(json["beeTypes"] is JArray beeTypes) ||
                !(json["humanTypes"] is JArray humanTypes) ||
                json["level"] == null ||
                !(json["squads"] is JArray squads) ||
                squads.Count > MatchSession.MaxLobbySquads)
            {
                return false;
            }

            MatchLobbySnapshot parsed = new MatchLobbySnapshot
            {
                Version = (int)version,
                MatchId = matchId,
                SetupSeed = (int)setupSeed,
                AuthorityPeerId = (int)authorityPeerId
            };

            for (int i = 0; i < peers.Count; i++)
            {
                if (!(peers[i] is JObject peerJson) ||
                    !HasExactFields(peerJson, LobbyPeerFields) ||
                    !TryReadInt64(peerJson, "id", out long peerId) ||
                    peerId <= 0 ||
                    peerId > int.MaxValue ||
                    !TryReadString(peerJson, "identity", out string identity) ||
                    string.IsNullOrWhiteSpace(identity) ||
                    identity.Length > MatchSession.MaxTransportIdentityLength)
                {
                    return false;
                }

                parsed.Peers.Add(new MatchLobbyPeerSnapshot((int)peerId, identity));
            }

            for (int i = 0; i < players.Count; i++)
            {
                if (!(players[i] is JObject playerJson) ||
                    !HasExactFields(playerJson, LobbyPlayerFields) ||
                    !TryReadInt64(playerJson, "id", out long playerId) ||
                    playerId <= MatchSession.UnownedPlayerId ||
                    playerId > int.MaxValue ||
                    !TryReadInt64(playerJson, "peer", out long peerId) ||
                    peerId <= 0 ||
                    peerId > int.MaxValue ||
                    !TryReadInt64(playerJson, "side", out long side) ||
                    side < int.MinValue ||
                    side > int.MaxValue)
                {
                    return false;
                }

                parsed.Players.Add(new MatchLobbyPlayerSnapshot(
                    (int)playerId,
                    (int)peerId,
                    (int)side));
            }

            for (int i = 0; i < beeTypes.Count; i++)
            {
                if (beeTypes[i].Type != JTokenType.Integer)
                {
                    return false;
                }
                long value = beeTypes[i].Value<long>();
                if (value < int.MinValue || value > int.MaxValue)
                {
                    return false;
                }
                parsed.BeeRandomShipTypes.Add((int)value);
            }

            for (int i = 0; i < humanTypes.Count; i++)
            {
                if (humanTypes[i].Type != JTokenType.Integer)
                {
                    return false;
                }
                long value = humanTypes[i].Value<long>();
                if (value < int.MinValue || value > int.MaxValue)
                {
                    return false;
                }
                parsed.HumanRandomShipTypes.Add((int)value);
            }

            JToken levelToken = json["level"];
            if (levelToken.Type != JTokenType.Null)
            {
                if (!(levelToken is JObject levelJson) ||
                    !HasExactFields(levelJson, LobbyLevelFields) ||
                    !TryReadInt64(levelJson, "id", out long levelId) ||
                    levelId < int.MinValue || levelId > int.MaxValue ||
                    !TryReadInt64(levelJson, "side", out long levelSide) ||
                    levelSide < int.MinValue || levelSide > int.MaxValue ||
                    !TryReadString(levelJson, "name", out string levelName) ||
                    string.IsNullOrEmpty(levelName) ||
                    levelName.Length > MatchSession.MaxLobbyNameLength ||
                    !TryReadInt64(levelJson, "map", out long mapIndex) ||
                    mapIndex < int.MinValue || mapIndex > int.MaxValue ||
                    !TryReadString(levelJson, "obstacles", out string obstacles) ||
                    obstacles.Length > MatchSession.MaxLobbyNameLength ||
                    !TryReadInt64(levelJson, "asteroids", out long asteroidOption) ||
                    asteroidOption < int.MinValue || asteroidOption > int.MaxValue ||
                    !TryReadInt64(levelJson, "fog", out long fog) ||
                    fog < int.MinValue || fog > int.MaxValue ||
                    !TryReadInt64(levelJson, "mining", out long mining) ||
                    mining < int.MinValue || mining > int.MaxValue ||
                    !TryReadBool(levelJson, "intro", out bool intro) ||
                    !TryReadBool(levelJson, "actionBox", out bool actionBox) ||
                    !TryReadInt64(levelJson, "supply", out long supply) ||
                    supply < int.MinValue || supply > int.MaxValue ||
                    !TryReadInt64(levelJson, "reinforceOption", out long reinforceOption) ||
                    reinforceOption < int.MinValue || reinforceOption > int.MaxValue ||
                    !TryReadInt64(levelJson, "reinforceDelay", out long reinforceDelay) ||
                    reinforceDelay < int.MinValue || reinforceDelay > int.MaxValue ||
                    !TryReadInt64(levelJson, "enemyType", out long enemyType) ||
                    enemyType < int.MinValue || enemyType > int.MaxValue ||
                    !TryReadInt64(levelJson, "enemyCount", out long enemyCount) ||
                    enemyCount < int.MinValue || enemyCount > int.MaxValue ||
                    !TryReadString(levelJson, "report", out string report) ||
                    report.Length > MatchSession.MaxLobbyReportLength ||
                    !TryReadFloat(levelJson, "beeX", out float beeX) ||
                    !TryReadFloat(levelJson, "beeY", out float beeY) ||
                    !TryReadFloat(levelJson, "humanX", out float humanX) ||
                    !TryReadFloat(levelJson, "humanY", out float humanY) ||
                    !(levelJson["obstacleList"] is JArray obstacleList) ||
                    obstacleList.Count > MatchSession.MaxLobbyObstacles)
                {
                    return false;
                }

                MatchLobbyLevelSnapshot parsedLevel = new MatchLobbyLevelSnapshot
                {
                    Id = (int)levelId,
                    Side = (int)levelSide,
                    Name = levelName,
                    MapIndex = (int)mapIndex,
                    Obstacles = obstacles,
                    AsteroidOption = (int)asteroidOption,
                    FogOfWar = (int)fog,
                    Mining = (int)mining,
                    HasPreLevelIntro = intro,
                    HasSquadActionBox = actionBox,
                    SupplyCapacity = (int)supply,
                    EnemyReinforcementsOption = (int)reinforceOption,
                    EnemyReinforcementDelay = (int)reinforceDelay,
                    EnemyShipTypeOption = (int)enemyType,
                    EnemySquadGenerationCount = (int)enemyCount,
                    EnemyReport = report,
                    BeeStartingX = beeX,
                    BeeStartingY = beeY,
                    HumanStartingX = humanX,
                    HumanStartingY = humanY
                };

                for (int i = 0; i < obstacleList.Count; i++)
                {
                    if (!(obstacleList[i] is JObject obstacleJson) ||
                        !HasExactFields(obstacleJson, LobbyObstacleFields) ||
                        !TryReadFloat(obstacleJson, "px", out float px) ||
                        !TryReadFloat(obstacleJson, "py", out float py) ||
                        !TryReadFloat(obstacleJson, "sx", out float sx) ||
                        !TryReadFloat(obstacleJson, "sy", out float sy))
                    {
                        return false;
                    }

                    parsedLevel.ObstacleList.Add(new MatchLobbyObstacleSnapshot
                    {
                        PositionX = px,
                        PositionY = py,
                        ScaleX = sx,
                        ScaleY = sy
                    });
                }

                parsed.Level = parsedLevel;
            }

            for (int i = 0; i < squads.Count; i++)
            {
                if (!(squads[i] is JObject squadJson) ||
                    !HasExactFields(squadJson, LobbySquadFields) ||
                    !TryReadInt64(squadJson, "role", out long role) ||
                    role < int.MinValue || role > int.MaxValue ||
                    !TryReadString(squadJson, "token", out string token) ||
                    !TryReadInt64(squadJson, "owner", out long owner) ||
                    owner <= MatchSession.UnownedPlayerId ||
                    owner > int.MaxValue ||
                    !TryReadInt64(squadJson, "id", out long squadId) ||
                    squadId >= 0 ||
                    !TryReadInt64(squadJson, "side", out long side) ||
                    side < int.MinValue || side > int.MaxValue ||
                    !TryReadString(squadJson, "name", out string name) ||
                    string.IsNullOrEmpty(name) ||
                    name.Length > MatchSession.MaxLobbyNameLength ||
                    !TryReadFloat(squadJson, "sx", out float sx) ||
                    !TryReadFloat(squadJson, "sy", out float sy) ||
                    !TryReadFloat(squadJson, "r", out float r) ||
                    !TryReadFloat(squadJson, "g", out float g) ||
                    !TryReadFloat(squadJson, "b", out float b) ||
                    !TryReadFloat(squadJson, "a", out float a) ||
                    !TryReadBool(squadJson, "cease", out bool cease) ||
                    !TryReadBool(squadJson, "matching", out bool matching) ||
                    !TryReadBool(squadJson, "chase", out bool chase) ||
                    !TryReadInt64(squadJson, "strategy", out long strategy) ||
                    strategy < int.MinValue || strategy > int.MaxValue ||
                    !(squadJson["ships"] is JArray ships) ||
                    ships.Count == 0 ||
                    ships.Count > MatchSession.MaxLobbyShipsPerSquad)
                {
                    return false;
                }

                MatchLobbySquadSnapshot squad = new MatchLobbySquadSnapshot
                {
                    Role = (int)role,
                    OwnershipToken = token,
                    OwnerPlayerId = (int)owner,
                    TransientSquadId = squadId,
                    Side = (int)side,
                    Name = name,
                    StartingX = sx,
                    StartingY = sy,
                    ColorR = r,
                    ColorG = g,
                    ColorB = b,
                    ColorA = a,
                    CeaseFire = cease,
                    IsMatchingSpeed = matching,
                    IsSetToChase = chase,
                    ShootingStrategy = (int)strategy
                };

                for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                {
                    if (!(ships[shipIndex] is JObject shipJson) ||
                        !HasExactFields(shipJson, LobbyShipFields) ||
                        !TryReadInt64(shipJson, "id", out long fleetId) ||
                        fleetId >= 0 ||
                        !TryReadInt64(shipJson, "type", out long shipType) ||
                        shipType < int.MinValue || shipType > int.MaxValue ||
                        !TryReadString(shipJson, "name", out string shipName) ||
                        string.IsNullOrEmpty(shipName) ||
                        shipName.Length > MatchSession.MaxLobbyNameLength ||
                        !TryReadFloat(shipJson, "ox", out float ox) ||
                        !TryReadFloat(shipJson, "oy", out float oy))
                    {
                        return false;
                    }

                    squad.Ships.Add(new MatchLobbyShipSnapshot
                    {
                        TransientFleetId = fleetId,
                        ShipType = (int)shipType,
                        Name = shipName,
                        OffsetX = ox,
                        OffsetY = oy
                    });
                }

                parsed.Squads.Add(squad);
            }

            if (!MatchSession.IsValidLobbySnapshot(parsed))
            {
                return false;
            }

            snapshot = parsed;
            return true;
        }

        public static bool TrySerializeAcknowledgement(
            Guid matchId,
            int playerId,
            long sequence,
            out byte[] payload)
        {
            payload = null;
            if (matchId == Guid.Empty ||
                playerId <= MatchSession.UnownedPlayerId ||
                sequence <= 0)
            {
                return false;
            }

            JObject json = new JObject
            {
                ["v"] = Version,
                ["match"] = matchId.ToString("N"),
                ["type"] = AcknowledgementPacketType,
                ["player"] = playerId,
                ["seq"] = sequence
            };

            byte[] encoded = StrictUtf8.GetBytes(json.ToString(Formatting.None));
            if (encoded.Length == 0 || encoded.Length > MaxPacketBytes)
            {
                return false;
            }

            payload = encoded;
            return true;
        }

        public static bool TryDeserializeAcknowledgement(
            byte[] payload,
            Guid expectedMatchId,
            out int playerId,
            out long sequence)
        {
            playerId = MatchSession.UnownedPlayerId;
            sequence = 0;
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
                if (!AcknowledgementFields.Contains(property.Name))
                {
                    return false;
                }
            }
            if (json.Count != AcknowledgementFields.Count ||
                !TryReadInt64(json, "v", out long version) || version != Version ||
                !TryReadString(json, "type", out string packetType) ||
                packetType != AcknowledgementPacketType ||
                !TryReadString(json, "match", out string matchText) ||
                !Guid.TryParseExact(matchText, "N", out Guid matchId) ||
                matchId != expectedMatchId ||
                !TryReadInt64(json, "player", out long playerIdValue) ||
                playerIdValue <= MatchSession.UnownedPlayerId ||
                playerIdValue > int.MaxValue ||
                !TryReadInt64(json, "seq", out sequence) ||
                sequence <= 0)
            {
                sequence = 0;
                return false;
            }

            playerId = (int)playerIdValue;
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

        private static bool HasExactFields(JObject json, HashSet<string> fields)
        {
            if (json == null || json.Count != fields.Count)
            {
                return false;
            }

            foreach (JProperty property in json.Properties())
            {
                if (!fields.Contains(property.Name))
                {
                    return false;
                }
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

        private static bool TryReadBool(JObject json, string name, out bool value)
        {
            value = false;
            JToken token = json[name];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                return false;
            }

            value = token.Value<bool>();
            return true;
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


    public interface IMultiplayerLobbyTransport : IDisposable
    {
        bool IsAvailable { get; }
        void Update();
        bool TryTakeReceivedSession(out MatchSession session);
    }

    public interface IMultiplayerTransport : IDisposable
    {
        bool IsAvailable { get; }
        void Update();
    }

}
