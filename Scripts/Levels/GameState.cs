using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels.Commands;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class GameState : MonoBehaviour
    {
        public HashSet<Projectile> Projectiles = new HashSet<Projectile>(ReferenceIdentityComparer<Projectile>.Instance);
        public List<Ship> Ships = new List<Ship>();
        public readonly List<Ship>[] ShipsBySide =
        {
            new List<Ship>(),
            new List<Ship>()
        };
        public List<Ship> ShipsToRelease = new List<Ship>();
        public Dictionary<long, Ship> ShipsById = new Dictionary<long, Ship>();
        public List<Squad> Squads = new List<Squad>();
        public List<Squad> SquadsToRelease = new List<Squad>();
        public Queue<Squad> SquadsAwaitingCommands = new Queue<Squad>();
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public List<Command> CommandsToRelease = new List<Command>();
        public List<Squad> SelectedSquads = new List<Squad>();
        public List<Obstacle> Obstacles = new List<Obstacle>();
        public List<AsteroidPiece> AsteroidPiecesToRelease = new List<AsteroidPiece>();
        public List<CollisionAsteroid> AsteroidsToRelease = new List<CollisionAsteroid>();
        public List<MiningAsteroid> MiningAsteroidsToRelease = new List<MiningAsteroid>();
        public List<FogOfWarVision> FogOfWarVisions = new List<FogOfWarVision>();
        public List<TargetingSquadMarker> TargetingSquadMarkers = new List<TargetingSquadMarker>();
        // Primary-local compatibility view used by existing campaign/UI code.
        public HashSet<MapObject> PlayerVisibleMapObjects = new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance);
        private readonly HashSet<MapObject>[] _nonPrimaryPlayerVisibleMapObjectsBySide =
        {
            new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance),
            new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance)
        };

        public int UserCommands, AICommands;
        public Guid MatchId { get; private set; }
        public int MatchLevelId { get; private set; }
        public bool IsPaused;
        public bool GameOver;
        public bool LevelEnded;
        private bool _hasEliminationSnapshot;
        private readonly bool[] _eliminationSnapshot = new bool[2];
        public int[] InitialTsv = { 0, 0 };
        public int[] OriginalSquadCounts = { 0, 0 };
        public Level Level;
        public Stage Stage;
        public HashSet<Ship>[] VisionCache =
        {
            new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance),
            new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance)
        };
        public HashSet<MiningAsteroid>[] HiveMindMiningAsteroidCache =
        {
            new HashSet<MiningAsteroid>(ReferenceIdentityComparer<MiningAsteroid>.Instance),
            new HashSet<MiningAsteroid>(ReferenceIdentityComparer<MiningAsteroid>.Instance)
        };
        public HashSet<Obstacle>[] HiveMindObstacleCache =
        {
            new HashSet<Obstacle>(ReferenceIdentityComparer<Obstacle>.Instance),
            new HashSet<Obstacle>(ReferenceIdentityComparer<Obstacle>.Instance)
        };
        public HashSet<MapObject>[] HiveMindMapObjectCache =
        {
            new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance),
            new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance)
        };
        internal readonly int[] HiveMindMapObjectRefreshFrame = { -1, -1 };
        public Dictionary<long, HashSet<Ship>>[] HivemindShips =
        {
            new Dictionary<long, HashSet<Ship>>(),
            new Dictionary<long, HashSet<Ship>>()
        };
        public List<ShipRemains> Deadbodies = new List<ShipRemains>();
        public HashSet<RocketExplosion> FireBargeExplosions = new HashSet<RocketExplosion>(ReferenceIdentityComparer<RocketExplosion>.Instance);
        public HashSet<MiningAsteroid> MiningAsteroids = new HashSet<MiningAsteroid>(ReferenceIdentityComparer<MiningAsteroid>.Instance);
        public HashSet<Ship> MiningShips = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public bool HasWarpGates, HasSelectedSquads, HasBeehives;
        public List<ShipDamageStatus>[] ShipDamageStatuses =
        {
            new List<ShipDamageStatus>(),
            new List<ShipDamageStatus>()
        };
        public Dictionary<long, ShipDamageStatus>[] ShipDamageStatusesById =
        {
            new Dictionary<long, ShipDamageStatus>(),
            new Dictionary<long, ShipDamageStatus>()
        };
        public Dictionary<long, int> OutcomeIdToPastCommandIndex = new Dictionary<long, int>();

        public int PlayerMineralsMined;
        public int EnemyShipsDestroyedByPlayer;
        public int PlayerShipsReturned;
        public int PlayerShipsLost;
        public readonly Dictionary<ConfigData.ShipTypes, int> PlayerShipsLostByType =
            new Dictionary<ConfigData.ShipTypes, int>();
        public int PlayerNewShipsReceived;
        public int PlayerScore;
        public int PlayerMineralsReceived;

        public void Setup(Level level)
        {
            Level = level;
            Stage = Level.Stage;
            MatchId = Stage != null && Stage.MatchSession != null
                ? Stage.MatchSession.MatchId
                : Guid.Empty;
            MatchLevelId = Level != null ? Level.MatchLevelId : 0;
        }

        public HashSet<MapObject> GetPlayerVisibleMapObjects(int side)
        {
            if (side != ConfigData.Configuration.BeeSide && side != ConfigData.Configuration.HumanSide)
            {
                return null;
            }

            if (ConfigData.Configuration != null && side == ConfigData.Configuration.UserSide)
            {
                return PlayerVisibleMapObjects;
            }

            return _nonPrimaryPlayerVisibleMapObjectsBySide[side - 1];
        }

        public void RecordPlayerShipLost(ConfigData.ShipTypes shipType)
        {
            PlayerShipsLost++;
            int count;
            PlayerShipsLostByType.TryGetValue(shipType, out count);
            PlayerShipsLostByType[shipType] = count + 1;
        }

        public void CaptureEliminationState()
        {
            if (_hasEliminationSnapshot)
            {
                return;
            }

            if (Level != null &&
                (Level.WinningSide == ConfigData.Configuration.HumanSide ||
                 Level.WinningSide == ConfigData.Configuration.BeeSide))
            {
                for (int side = 1; side <= _eliminationSnapshot.Length; side++)
                {
                    _eliminationSnapshot[side - 1] = side != Level.WinningSide;
                }
            }
            else
            {
                for (int sideIndex = 0; sideIndex < ShipsBySide.Length; sideIndex++)
                {
                    List<Ship> sideShips = ShipsBySide[sideIndex];
                    bool hasMobileShip = false;
                    for (int shipIndex = 0; shipIndex < sideShips.Count; shipIndex++)
                    {
                        if (sideShips[shipIndex].IsMobile)
                        {
                            hasMobileShip = true;
                            break;
                        }
                    }
                    _eliminationSnapshot[sideIndex] = !hasMobileShip;
                }
            }
            _hasEliminationSnapshot = true;
        }

        public bool TryGetCapturedEliminationState(int side, out bool isKilled)
        {
            if (_hasEliminationSnapshot && side >= 1 && side <= _eliminationSnapshot.Length)
            {
                isKilled = _eliminationSnapshot[side - 1];
                return true;
            }

            isKilled = false;
            return false;
        }

        public void ResetState()
        {
            CleanupRuntimeObjectsForReset();
            Stage?.MatchSession?.RemoveOutgoingPlayerCommandsForLevel(MatchLevelId);

            if (Level != null)
            {
                Level.Pathfinder = null;
            }

            Ships.Clear();
            for (int side = 0; side < ShipsBySide.Length; side++)
            {
                ShipsBySide[side].Clear();
            }
            ShipsById.Clear();
            Squads.Clear();
            ClearSquadsAwaitingHiveMindCommands();
            ClearQueuedPlayerCommands();
            PastCommands.Clear();
            OutcomeIdToPastCommandIndex.Clear();
            SelectedSquads.Clear();
            ResetPlayerSelectionState();
            PlayerVisibleMapObjects.Clear();
            for (int side = 0; side < _nonPrimaryPlayerVisibleMapObjectsBySide.Length; side++)
            {
                _nonPrimaryPlayerVisibleMapObjectsBySide[side].Clear();
            }
            Obstacles.Clear();
            FogOfWarVisions.Clear();
            for (int side = 0; side < 2; side++)
            {
                InitialTsv[side] = 0;
                OriginalSquadCounts[side] = 0;
                if (HivemindShips[side] == null) HivemindShips[side] = new Dictionary<long, HashSet<Ship>>();
                else HivemindShips[side].Clear();
                if (VisionCache[side] == null) VisionCache[side] = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
                else VisionCache[side].Clear();
                if (HiveMindMiningAsteroidCache[side] == null)
                    HiveMindMiningAsteroidCache[side] = new HashSet<MiningAsteroid>(ReferenceIdentityComparer<MiningAsteroid>.Instance);
                else HiveMindMiningAsteroidCache[side].Clear();
                if (HiveMindObstacleCache[side] == null)
                    HiveMindObstacleCache[side] = new HashSet<Obstacle>(ReferenceIdentityComparer<Obstacle>.Instance);
                else HiveMindObstacleCache[side].Clear();
                if (HiveMindMapObjectCache[side] == null)
                    HiveMindMapObjectCache[side] = new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance);
                else HiveMindMapObjectCache[side].Clear();
                HiveMindMapObjectRefreshFrame[side] = -1;
                ShipDamageStatuses[side].Clear();
                ShipDamageStatusesById[side].Clear();
            }
            Deadbodies.Clear();
            FireBargeExplosions.Clear();
            MiningAsteroids.Clear();
            MiningShips.Clear();
            ShipsToRelease.Clear();
            SquadsToRelease.Clear();
            CommandsToRelease.Clear();
            AsteroidsToRelease.Clear();
            AsteroidPiecesToRelease.Clear();
            MiningAsteroidsToRelease.Clear();
            Projectiles.Clear();
            TargetingSquadMarkers.Clear();

            PlayerMineralsMined = 0;
            EnemyShipsDestroyedByPlayer = 0;
            PlayerShipsReturned = 0;
            PlayerShipsLost = 0;
            PlayerShipsLostByType.Clear();
            PlayerNewShipsReceived = 0;
            PlayerScore = 0;
            PlayerMineralsReceived = 0;
            UserCommands = 0;
            AICommands = 0;
            HasSelectedSquads = false;
            HasWarpGates = false;
            HasBeehives = false;
            IsPaused = false;
            GameOver = false;
            LevelEnded = false;
            _hasEliminationSnapshot = false;
            _eliminationSnapshot[0] = false;
            _eliminationSnapshot[1] = false;
        }
    }

    /// <summary>
    /// Transient identity for one participant in a Free Play match. Player ids are match-local
    /// and are deliberately separate from account, persistent fleet, and pooled runtime ids.
    /// </summary>
    public sealed class MatchPeer
    {
        public int Id { get; }
        public bool IsLocal { get; }
        public string TransportIdentity { get; private set; }

        public MatchPeer(int id, bool isLocal, string transportIdentity)
        {
            Id = id;
            IsLocal = isLocal;
            TransportIdentity = transportIdentity ?? string.Empty;
        }

        internal void SetTransportIdentity(string transportIdentity)
        {
            TransportIdentity = transportIdentity ?? string.Empty;
        }
    }

    public sealed class MatchPlayer
    {
        public int Id { get; }
        public int PeerId { get; }
        public int Side { get; private set; }
        public bool IsLocal { get; }

        public MatchPlayer(int id, int peerId, int side, bool isLocal)
        {
            Id = id;
            PeerId = peerId;
            Side = side;
            IsLocal = isLocal;
        }

        internal void SetSide(int side)
        {
            Side = side;
        }
    }

    public enum MatchSessionPhase
    {
        Lobby,
        Battle,
        Ended
    }

    [Serializable]
    public sealed class MatchLobbyPeerSnapshot
    {
        public int PeerId;
        public string TransportIdentity;

        public MatchLobbyPeerSnapshot()
        {
        }

        public MatchLobbyPeerSnapshot(int peerId, string transportIdentity)
        {
            PeerId = peerId;
            TransportIdentity = transportIdentity ?? string.Empty;
        }
    }

    [Serializable]
    public sealed class MatchLobbyPlayerSnapshot
    {
        public int PlayerId;
        public int PeerId;
        public int Side;

        public MatchLobbyPlayerSnapshot()
        {
        }

        public MatchLobbyPlayerSnapshot(int playerId, int peerId, int side)
        {
            PlayerId = playerId;
            PeerId = peerId;
            Side = side;
        }
    }

    [Serializable]
    public sealed class MatchLobbyShipSnapshot
    {
        public long TransientFleetId;
        public int ShipType;
        public string Name;
        public float OffsetX;
        public float OffsetY;
    }

    [Serializable]
    public sealed class MatchLobbySquadSnapshot
    {
        public string OwnershipToken;
        public int OwnerPlayerId;
        public long TransientSquadId;
        public int Side;
        public string Name;
        public float StartingX;
        public float StartingY;
        public float ColorR;
        public float ColorG;
        public float ColorB;
        public float ColorA;
        public bool CeaseFire;
        public bool IsMatchingSpeed;
        public bool IsSetToChase;
        public int ShootingStrategy;
        public List<MatchLobbyShipSnapshot> Ships = new List<MatchLobbyShipSnapshot>();
    }

    [Serializable]
    public sealed class MatchLobbySnapshot
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string MatchId;
        public int AuthorityPeerId;
        public List<MatchLobbyPeerSnapshot> Peers = new List<MatchLobbyPeerSnapshot>();
        public List<MatchLobbyPlayerSnapshot> Players = new List<MatchLobbyPlayerSnapshot>();
        public List<MatchLobbySquadSnapshot> Squads = new List<MatchLobbySquadSnapshot>();
    }

    /// <summary>
    /// Transient Free Play participant/ownership state. Campaign and Challenge do not create
    /// this session; their existing single-player ownership path remains unchanged.
    /// </summary>
    public sealed class MatchSession
    {
        public const int UnownedPlayerId = 0;
        public const int LegacyLocalPlayerId = 1;
        public const int LocalPeerId = 1;
        public const int MaxLobbyPeers = 16;
        public const int MaxLobbyPlayers = 32;
        public const int MaxLobbySquads = 64;
        public const int MaxLobbyShipsPerSquad = 32;
        public const int MaxLobbyNameLength = 128;
        public const int MaxTransportIdentityLength = 256;
        private const int LegacyRemotePeerIdOffset = 1000000;

        private readonly List<MatchPeer> _peers = new List<MatchPeer>();
        private readonly List<MatchPlayer> _players = new List<MatchPlayer>();
        private long _nextMatchSquadId = 1;
        private readonly Dictionary<int, long> _nextPlayerCommandSequences = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _lastAcceptedPlayerCommandSequences = new Dictionary<int, long>();
        private readonly Queue<(int MatchLevelId, PlayerCommandEnvelope Command)> _outgoingPlayerCommands =
            new Queue<(int MatchLevelId, PlayerCommandEnvelope Command)>();
        private readonly Queue<(int TargetPeerId, int PlayerId, long Sequence)> _outgoingCommandAcknowledgements =
            new Queue<(int TargetPeerId, int PlayerId, long Sequence)>();
        private readonly object _outgoingPlayerCommandsLock = new object();
        private readonly object _outgoingCommandAcknowledgementsLock = new object();
        public const int MaxOutgoingPlayerCommands = 1024;
        public const int MaxOutgoingCommandAcknowledgements = 1024;
        private readonly Dictionary<Guid, (int PlayerId, int Side, SavedSquad Squad)> _squadOwnerAssignments =
            new Dictionary<Guid, (int PlayerId, int Side, SavedSquad Squad)>();

        public IReadOnlyList<MatchPeer> Peers => _peers;
        public IReadOnlyList<MatchPlayer> Players => _players;
        public Guid MatchId { get; private set; } = Guid.NewGuid();
        public int AuthorityPeerId { get; private set; }
        public int PrimaryLocalPlayerId { get; private set; } = UnownedPlayerId;
        public MatchSessionPhase Phase { get; private set; } = MatchSessionPhase.Lobby;
        public bool IsMultiplayer => _players.Count > 1;
        public bool IsConfiguring => Phase == MatchSessionPhase.Lobby;

        public static MatchSession CreateSolo(int side)
        {
            MatchSession session = new MatchSession();
            session.AddPlayer(LegacyLocalPlayerId, side, true);
            return session;
        }

        public static bool IsValidLobbySnapshot(MatchLobbySnapshot snapshot)
        {
            if (snapshot == null ||
                snapshot.Version != MatchLobbySnapshot.CurrentVersion ||
                string.IsNullOrWhiteSpace(snapshot.MatchId) ||
                !Guid.TryParseExact(snapshot.MatchId, "N", out Guid matchId) ||
                matchId == Guid.Empty ||
                snapshot.AuthorityPeerId <= 0 ||
                snapshot.Peers == null ||
                snapshot.Peers.Count == 0 ||
                snapshot.Peers.Count > MaxLobbyPeers ||
                snapshot.Players == null ||
                snapshot.Players.Count == 0 ||
                snapshot.Players.Count > MaxLobbyPlayers)
            {
                return false;
            }

            HashSet<int> peerIds = new HashSet<int>();
            HashSet<string> transportIdentities = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Peers.Count; i++)
            {
                MatchLobbyPeerSnapshot peer = snapshot.Peers[i];
                if (peer == null ||
                    peer.PeerId <= 0 ||
                    string.IsNullOrWhiteSpace(peer.TransportIdentity) ||
                    peer.TransportIdentity.Length > MaxTransportIdentityLength ||
                    !peerIds.Add(peer.PeerId) ||
                    !transportIdentities.Add(peer.TransportIdentity))
                {
                    return false;
                }
            }

            if (!peerIds.Contains(snapshot.AuthorityPeerId))
            {
                return false;
            }

            HashSet<int> playerIds = new HashSet<int>();
            Dictionary<int, int> playerSides = new Dictionary<int, int>();
            for (int i = 0; i < snapshot.Players.Count; i++)
            {
                MatchLobbyPlayerSnapshot player = snapshot.Players[i];
                if (player == null ||
                    player.PlayerId <= UnownedPlayerId ||
                    !playerIds.Add(player.PlayerId) ||
                    !peerIds.Contains(player.PeerId) ||
                    (player.Side != ConfigData.Configuration.BeeSide &&
                     player.Side != ConfigData.Configuration.HumanSide))
                {
                    return false;
                }
                playerSides.Add(player.PlayerId, player.Side);
            }

            if (snapshot.Squads == null || snapshot.Squads.Count > MaxLobbySquads)
            {
                return false;
            }

            HashSet<Guid> ownershipTokens = new HashSet<Guid>();
            HashSet<long> transientSquadIds = new HashSet<long>();
            HashSet<long> transientFleetIds = new HashSet<long>();
            for (int i = 0; i < snapshot.Squads.Count; i++)
            {
                MatchLobbySquadSnapshot squad = snapshot.Squads[i];
                if (squad == null ||
                    string.IsNullOrWhiteSpace(squad.OwnershipToken) ||
                    !Guid.TryParseExact(squad.OwnershipToken, "N", out Guid ownershipToken) ||
                    ownershipToken == Guid.Empty ||
                    !ownershipTokens.Add(ownershipToken) ||
                    squad.OwnerPlayerId <= UnownedPlayerId ||
                    !playerSides.TryGetValue(squad.OwnerPlayerId, out int ownerSide) ||
                    ownerSide != squad.Side ||
                    squad.TransientSquadId >= 0 ||
                    !transientSquadIds.Add(squad.TransientSquadId) ||
                    string.IsNullOrEmpty(squad.Name) ||
                    squad.Name.Length > MaxLobbyNameLength ||
                    !IsFiniteLobbyFloat(squad.StartingX) ||
                    !IsFiniteLobbyFloat(squad.StartingY) ||
                    !IsFiniteLobbyFloat(squad.ColorR) ||
                    !IsFiniteLobbyFloat(squad.ColorG) ||
                    !IsFiniteLobbyFloat(squad.ColorB) ||
                    !IsFiniteLobbyFloat(squad.ColorA) ||
                    !Enum.IsDefined(typeof(ConfigData.ShootingStrategyTypes), squad.ShootingStrategy) ||
                    squad.Ships == null ||
                    squad.Ships.Count == 0 ||
                    squad.Ships.Count > MaxLobbyShipsPerSquad)
                {
                    return false;
                }

                for (int shipIndex = 0; shipIndex < squad.Ships.Count; shipIndex++)
                {
                    MatchLobbyShipSnapshot ship = squad.Ships[shipIndex];
                    if (ship == null ||
                        ship.TransientFleetId >= 0 ||
                        !transientFleetIds.Add(ship.TransientFleetId) ||
                        !Enum.IsDefined(typeof(ConfigData.ShipTypes), ship.ShipType) ||
                        string.IsNullOrEmpty(ship.Name) ||
                        ship.Name.Length > MaxLobbyNameLength ||
                        !IsFiniteLobbyFloat(ship.OffsetX) ||
                        !IsFiniteLobbyFloat(ship.OffsetY))
                    {
                        return false;
                    }

                    ConfigData.ShipTypes shipType = (ConfigData.ShipTypes)ship.ShipType;
                    if (!Utilities.ConvertShipTypeToSide.TryGetValue(shipType, out int shipSide) ||
                        shipSide != squad.Side)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsFiniteLobbyFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public bool TryCreateLobbySnapshot(out MatchLobbySnapshot snapshot)
        {
            snapshot = null;
            if (!IsConfiguring ||
                MatchId == Guid.Empty ||
                AuthorityPeerId <= 0 ||
                _peers.Count == 0 ||
                _peers.Count > MaxLobbyPeers ||
                _players.Count == 0 ||
                _players.Count > MaxLobbyPlayers ||
                !_peers.Any(peer => peer.Id == AuthorityPeerId))
            {
                return false;
            }

            MatchLobbySnapshot candidate = new MatchLobbySnapshot
            {
                MatchId = MatchId.ToString("N"),
                AuthorityPeerId = AuthorityPeerId
            };

            HashSet<int> peerIds = new HashSet<int>();
            HashSet<string> transportIdentities = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _peers.Count; i++)
            {
                MatchPeer peer = _peers[i];
                if (peer == null ||
                    peer.Id <= 0 ||
                    string.IsNullOrWhiteSpace(peer.TransportIdentity) ||
                    peer.TransportIdentity.Length > MaxTransportIdentityLength ||
                    !peerIds.Add(peer.Id) ||
                    !transportIdentities.Add(peer.TransportIdentity))
                {
                    return false;
                }

                candidate.Peers.Add(new MatchLobbyPeerSnapshot(
                    peer.Id,
                    peer.TransportIdentity));
            }

            HashSet<int> playerIds = new HashSet<int>();
            for (int i = 0; i < _players.Count; i++)
            {
                MatchPlayer player = _players[i];
                if (player == null ||
                    player.Id <= UnownedPlayerId ||
                    !playerIds.Add(player.Id) ||
                    !peerIds.Contains(player.PeerId) ||
                    (player.Side != ConfigData.Configuration.BeeSide &&
                     player.Side != ConfigData.Configuration.HumanSide))
                {
                    return false;
                }

                candidate.Players.Add(new MatchLobbyPlayerSnapshot(
                    player.Id,
                    player.PeerId,
                    player.Side));
            }

            long nextTransientSquadId = -1;
            long nextTransientFleetId = -1;
            foreach (KeyValuePair<Guid, (int PlayerId, int Side, SavedSquad Squad)> assignment in
                _squadOwnerAssignments.OrderBy(pair => pair.Key))
            {
                SavedSquad source = assignment.Value.Squad;
                if (source == null ||
                    source.Side != assignment.Value.Side ||
                    source.GetSquadShips() == null ||
                    source.GetSquadShips().Count == 0)
                {
                    return false;
                }

                MatchLobbySquadSnapshot squadSnapshot = new MatchLobbySquadSnapshot
                {
                    OwnershipToken = assignment.Key.ToString("N"),
                    OwnerPlayerId = assignment.Value.PlayerId,
                    TransientSquadId = nextTransientSquadId--,
                    Side = source.Side,
                    Name = string.IsNullOrEmpty(source.Name) ? "Squad" : source.Name,
                    StartingX = source.StartingPosition.x,
                    StartingY = source.StartingPosition.y,
                    ColorR = source.Color.r,
                    ColorG = source.Color.g,
                    ColorB = source.Color.b,
                    ColorA = source.Color.a,
                    CeaseFire = source.CeaseFire,
                    IsMatchingSpeed = source.IsMatchingSpeed,
                    IsSetToChase = source.IsSetToChase,
                    ShootingStrategy = (int)source.ChosenShootingStrategy
                };

                List<SquadShip> sourceShips = source.GetSquadShips();
                for (int shipIndex = 0; shipIndex < sourceShips.Count; shipIndex++)
                {
                    SquadShip sourceShip = sourceShips[shipIndex];
                    FleetShip fleetShip = sourceShip?.GetFleetShip();
                    if (sourceShip == null || fleetShip == null)
                    {
                        return false;
                    }

                    squadSnapshot.Ships.Add(new MatchLobbyShipSnapshot
                    {
                        TransientFleetId = nextTransientFleetId--,
                        ShipType = (int)sourceShip.ShipType,
                        Name = string.IsNullOrEmpty(fleetShip.Name)
                            ? $"Ship-{shipIndex + 1}"
                            : fleetShip.Name,
                        OffsetX = sourceShip.Offset.x,
                        OffsetY = sourceShip.Offset.y
                    });
                }

                candidate.Squads.Add(squadSnapshot);
            }

            if (!IsValidLobbySnapshot(candidate))
            {
                return false;
            }

            snapshot = candidate;
            return true;
        }

        public static bool TryCreateFromLobbySnapshot(
            MatchLobbySnapshot snapshot,
            string localTransportIdentity,
            out MatchSession session)
        {
            session = null;
            if (!IsValidLobbySnapshot(snapshot) ||
                string.IsNullOrWhiteSpace(localTransportIdentity) ||
                localTransportIdentity.Length > MaxTransportIdentityLength ||
                !Guid.TryParseExact(snapshot.MatchId, "N", out Guid matchId))
            {
                return false;
            }

            MatchSession candidate = new MatchSession();
            if (!candidate.TrySetMatchId(matchId))
            {
                return false;
            }

            HashSet<int> peerIds = new HashSet<int>();
            HashSet<string> transportIdentities = new HashSet<string>(StringComparer.Ordinal);
            int localPeerCount = 0;
            for (int i = 0; i < snapshot.Peers.Count; i++)
            {
                MatchLobbyPeerSnapshot peer = snapshot.Peers[i];
                if (peer == null ||
                    peer.PeerId <= 0 ||
                    string.IsNullOrWhiteSpace(peer.TransportIdentity) ||
                    !peerIds.Add(peer.PeerId) ||
                    !transportIdentities.Add(peer.TransportIdentity))
                {
                    return false;
                }

                bool isLocal = string.Equals(
                    peer.TransportIdentity,
                    localTransportIdentity,
                    StringComparison.Ordinal);
                if (isLocal)
                {
                    localPeerCount++;
                }

                if (!candidate.AddPeer(
                        peer.PeerId,
                        isLocal,
                        peer.TransportIdentity))
                {
                    return false;
                }
            }

            if (localPeerCount != 1 ||
                !peerIds.Contains(snapshot.AuthorityPeerId) ||
                !candidate.TrySetAuthorityPeer(snapshot.AuthorityPeerId))
            {
                return false;
            }

            HashSet<int> playerIds = new HashSet<int>();
            int localPlayerCount = 0;
            for (int i = 0; i < snapshot.Players.Count; i++)
            {
                MatchLobbyPlayerSnapshot player = snapshot.Players[i];
                if (player == null ||
                    player.PlayerId <= UnownedPlayerId ||
                    !playerIds.Add(player.PlayerId) ||
                    !peerIds.Contains(player.PeerId) ||
                    (player.Side != ConfigData.Configuration.BeeSide &&
                     player.Side != ConfigData.Configuration.HumanSide) ||
                    !candidate.AddPlayerToPeer(
                        player.PlayerId,
                        player.Side,
                        player.PeerId))
                {
                    return false;
                }

                if (candidate.IsLocalPlayer(player.PlayerId))
                {
                    localPlayerCount++;
                }
            }

            if (localPlayerCount == 0 ||
                candidate.PrimaryLocalPlayerId == UnownedPlayerId)
            {
                return false;
            }

            for (int i = 0; i < snapshot.Squads.Count; i++)
            {
                MatchLobbySquadSnapshot squadSnapshot = snapshot.Squads[i];
                if (!Guid.TryParseExact(
                        squadSnapshot.OwnershipToken,
                        "N",
                        out Guid ownershipToken))
                {
                    return false;
                }

                SavedSquad squad = new SavedSquad(
                    squadSnapshot.TransientSquadId,
                    squadSnapshot.Side,
                    squadSnapshot.Name,
                    new Vector2(squadSnapshot.StartingX, squadSnapshot.StartingY),
                    squadSnapshot.CeaseFire,
                    squadSnapshot.IsMatchingSpeed,
                    (ConfigData.ShootingStrategyTypes)squadSnapshot.ShootingStrategy,
                    new Color(
                        squadSnapshot.ColorR,
                        squadSnapshot.ColorG,
                        squadSnapshot.ColorB,
                        squadSnapshot.ColorA),
                    new SquadStatBlock(
                        "Multiplayer",
                        0,
                        0,
                        0,
                        0,
                        0,
                        0));
                squad.IsSetToChase = squadSnapshot.IsSetToChase;
                squad.MatchOwnershipToken = ownershipToken;

                for (int shipIndex = 0; shipIndex < squadSnapshot.Ships.Count; shipIndex++)
                {
                    MatchLobbyShipSnapshot shipSnapshot = squadSnapshot.Ships[shipIndex];
                    FleetShip fleetShip = new FleetShip(
                        shipSnapshot.TransientFleetId,
                        (ConfigData.ShipTypes)shipSnapshot.ShipType,
                        false,
                        false,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        shipSnapshot.Name);
                    squad.AddShipToSquad(new SquadShip(
                        fleetShip,
                        new Vector2(shipSnapshot.OffsetX, shipSnapshot.OffsetY)));
                }

                if (!candidate.TryAssignSavedSquadOwner(
                        squad,
                        squadSnapshot.OwnerPlayerId))
                {
                    return false;
                }
            }

            session = candidate;
            return true;
        }

        public bool TrySetMatchId(Guid matchId)
        {
            if (!IsConfiguring || matchId == Guid.Empty)
            {
                return false;
            }

            MatchId = matchId;
            return true;
        }

        public bool AddPeer(int peerId, bool isLocal, string transportIdentity = null)
        {
            if (!IsConfiguring || peerId <= 0 || _peers.Any(peer => peer.Id == peerId))
            {
                return false;
            }
            if (isLocal && _peers.Any(peer => peer.IsLocal))
            {
                return false;
            }

            _peers.Add(new MatchPeer(peerId, isLocal, transportIdentity));
            if (isLocal && AuthorityPeerId == 0)
            {
                AuthorityPeerId = peerId;
            }
            return true;
        }

        public bool TrySetAuthorityPeer(int peerId)
        {
            if (!IsConfiguring || !_peers.Any(peer => peer.Id == peerId))
            {
                return false;
            }

            AuthorityPeerId = peerId;
            return true;
        }

        public bool TrySetPeerTransportIdentity(int peerId, string transportIdentity)
        {
            if (!IsConfiguring || string.IsNullOrWhiteSpace(transportIdentity))
            {
                return false;
            }

            MatchPeer peer = _peers.FirstOrDefault(candidate => candidate.Id == peerId);
            if (peer == null ||
                _peers.Any(candidate =>
                    candidate.Id != peerId &&
                    string.Equals(candidate.TransportIdentity, transportIdentity, StringComparison.Ordinal)))
            {
                return false;
            }

            peer.SetTransportIdentity(transportIdentity);
            return true;
        }

        public bool AddPlayer(int playerId, int side, bool isLocal)
        {
            int peerId;
            if (isLocal)
            {
                MatchPeer localPeer = _peers.FirstOrDefault(peer => peer.IsLocal);
                if (localPeer == null)
                {
                    if (!AddPeer(LocalPeerId, true))
                    {
                        return false;
                    }
                    localPeer = _peers.First(peer => peer.IsLocal);
                }
                peerId = localPeer.Id;
            }
            else
            {
                peerId = LegacyRemotePeerIdOffset + playerId;
                if (!_peers.Any(peer => peer.Id == peerId) && !AddPeer(peerId, false))
                {
                    return false;
                }
            }

            return AddPlayerToPeer(playerId, side, peerId);
        }

        public bool AddPlayerToPeer(int playerId, int side, int peerId)
        {
            if (!IsConfiguring || playerId <= UnownedPlayerId ||
                (side != ConfigData.Configuration.BeeSide && side != ConfigData.Configuration.HumanSide) ||
                _players.Any(player => player.Id == playerId))
            {
                return false;
            }

            MatchPeer peer = _peers.FirstOrDefault(candidate => candidate.Id == peerId);
            if (peer == null)
            {
                return false;
            }

            _players.Add(new MatchPlayer(playerId, peerId, side, peer.IsLocal));
            if (peer.IsLocal && PrimaryLocalPlayerId == UnownedPlayerId)
            {
                PrimaryLocalPlayerId = playerId;
            }
            return true;
        }

        public bool RemovePeer(int peerId)
        {
            if (!IsConfiguring)
            {
                return false;
            }

            MatchPeer peer = _peers.FirstOrDefault(candidate => candidate.Id == peerId);
            if (peer == null)
            {
                return false;
            }

            List<int> playerIds = _players
                .Where(player => player.PeerId == peerId)
                .Select(player => player.Id)
                .ToList();
            for (int i = 0; i < playerIds.Count; i++)
            {
                RemovePlayer(playerIds[i]);
            }
            _peers.Remove(peer);
            if (AuthorityPeerId == peerId)
            {
                AuthorityPeerId = 0;
            }
            return true;
        }

        public bool RemovePlayer(int playerId)
        {
            if (!IsConfiguring)
            {
                return false;
            }

            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            if (player == null)
            {
                return false;
            }

            _players.Remove(player);
            RemoveSquadAssignmentsForPlayer(playerId, null);
            if (PrimaryLocalPlayerId == playerId)
            {
                MatchPlayer replacement = _players.FirstOrDefault(candidate => candidate.IsLocal);
                PrimaryLocalPlayerId = replacement == null ? UnownedPlayerId : replacement.Id;
            }
            return true;
        }

        public bool TrySetPlayerSide(int playerId, int side)
        {
            if (!IsConfiguring ||
                (side != ConfigData.Configuration.BeeSide && side != ConfigData.Configuration.HumanSide))
            {
                return false;
            }

            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            if (player == null)
            {
                return false;
            }

            player.SetSide(side);
            RemoveSquadAssignmentsForPlayer(playerId, side);
            return true;
        }

        public bool SetPrimaryLocalPlayer(int playerId)
        {
            if (!IsConfiguring)
            {
                return false;
            }

            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            if (player == null || !player.IsLocal)
            {
                return false;
            }

            PrimaryLocalPlayerId = playerId;
            return true;
        }

        public int GetSolePlayerIdForSide(int side)
        {
            int ownerId = UnownedPlayerId;
            bool foundOwner = false;
            for (int i = 0; i < _players.Count; i++)
            {
                MatchPlayer player = _players[i];
                if (player.Side != side)
                {
                    continue;
                }
                if (foundOwner)
                {
                    return UnownedPlayerId;
                }

                ownerId = player.Id;
                foundOwner = true;
            }
            return ownerId;
        }

        public bool IsPrimaryLocalPlayer(int playerId)
        {
            return playerId != UnownedPlayerId && playerId == PrimaryLocalPlayerId;
        }

        public bool HasPlayer(int playerId)
        {
            return playerId > UnownedPlayerId && _players.Any(player => player.Id == playerId);
        }

        public bool IsLocalPlayer(int playerId)
        {
            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            return player != null && player.IsLocal;
        }

        public int GetPlayerPeerId(int playerId)
        {
            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            return player == null ? 0 : player.PeerId;
        }

        public bool DoesPeerOwnPlayer(int peerId, int playerId)
        {
            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            return player != null && player.PeerId == peerId;
        }

        public string GetPeerTransportIdentity(int peerId)
        {
            MatchPeer peer = _peers.FirstOrDefault(candidate => candidate.Id == peerId);
            return peer == null ? string.Empty : peer.TransportIdentity;
        }

        public bool HasRemotePeer => _peers.Any(peer => !peer.IsLocal);

        public bool IsLocalAuthority =>
            _peers.Any(peer => peer.Id == AuthorityPeerId && peer.IsLocal);

        public bool TryBeginBattle()
        {
            if (!IsConfiguring || MatchId == Guid.Empty ||
                AuthorityPeerId <= 0 || !_peers.Any(peer => peer.Id == AuthorityPeerId) ||
                _players.Count == 0 || PrimaryLocalPlayerId == UnownedPlayerId)
            {
                return false;
            }

            MatchPlayer primaryLocalPlayer = _players.FirstOrDefault(player =>
                player.Id == PrimaryLocalPlayerId && player.IsLocal);
            if (primaryLocalPlayer == null)
            {
                return false;
            }

            Phase = MatchSessionPhase.Battle;
            return true;
        }

        public bool EndBattle()
        {
            if (Phase != MatchSessionPhase.Battle)
            {
                return false;
            }

            ClearOutgoingPlayerCommands();
            ClearOutgoingCommandAcknowledgements();
            Phase = MatchSessionPhase.Ended;
            return true;
        }

        public long AllocateMatchSquadId()
        {
            return _nextMatchSquadId++;
        }

        public int GetPlayerSide(int playerId)
        {
            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            return player == null ? 0 : player.Side;
        }

        public long AllocatePlayerCommandSequence(int playerId)
        {
            if (Phase != MatchSessionPhase.Battle || !HasPlayer(playerId))
            {
                return 0;
            }

            if (!_nextPlayerCommandSequences.TryGetValue(playerId, out long nextSequence))
            {
                nextSequence = 1;
            }
            _nextPlayerCommandSequences[playerId] = nextSequence + 1;
            return nextSequence;
        }

        public bool QueueOutgoingPlayerCommand(
            int matchLevelId,
            PlayerCommandEnvelope command)
        {
            if (Phase != MatchSessionPhase.Battle ||
                IsLocalAuthority ||
                matchLevelId <= 0 ||
                command == null ||
                !IsLocalPlayer(command.PlayerId) ||
                command.Sequence <= 0)
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

            lock (_outgoingPlayerCommandsLock)
            {
                if (_outgoingPlayerCommands.Count >= MaxOutgoingPlayerCommands)
                {
                    return false;
                }

                _outgoingPlayerCommands.Enqueue((matchLevelId, queuedCopy));
                return true;
            }
        }

        public int CopyOutgoingPlayerCommands(
            List<(int MatchLevelId, PlayerCommandEnvelope Command)> destination,
            int maxCommands)
        {
            if (destination == null || maxCommands <= 0)
            {
                return 0;
            }

            destination.Clear();
            lock (_outgoingPlayerCommandsLock)
            {
                foreach ((int MatchLevelId, PlayerCommandEnvelope Command) queued in _outgoingPlayerCommands)
                {
                    PlayerCommandEnvelope source = queued.Command;
                    destination.Add((
                        queued.MatchLevelId,
                        new PlayerCommandEnvelope(
                            source.PlayerId,
                            source.Sequence,
                            source.Kind,
                            source.SquadCommandId,
                            source.TargetSquadCommandId,
                            source.PointA,
                            source.PointB)));
                    if (destination.Count >= maxCommands)
                    {
                        break;
                    }
                }
            }
            return destination.Count;
        }

        public void AcknowledgeOutgoingPlayerCommands(int playerId, long sequence)
        {
            if (playerId <= UnownedPlayerId || sequence <= 0)
            {
                return;
            }

            lock (_outgoingPlayerCommandsLock)
            {
                int count = _outgoingPlayerCommands.Count;
                for (int i = 0; i < count; i++)
                {
                    (int MatchLevelId, PlayerCommandEnvelope Command) queued =
                        _outgoingPlayerCommands.Dequeue();
                    if (queued.Command == null ||
                        queued.Command.PlayerId != playerId ||
                        queued.Command.Sequence > sequence)
                    {
                        _outgoingPlayerCommands.Enqueue(queued);
                    }
                }
            }
        }

        public void RemoveOutgoingPlayerCommandsForLevel(int matchLevelId)
        {
            if (matchLevelId <= 0)
            {
                return;
            }

            lock (_outgoingPlayerCommandsLock)
            {
                int count = _outgoingPlayerCommands.Count;
                for (int i = 0; i < count; i++)
                {
                    (int MatchLevelId, PlayerCommandEnvelope Command) queued =
                        _outgoingPlayerCommands.Dequeue();
                    if (queued.MatchLevelId != matchLevelId)
                    {
                        _outgoingPlayerCommands.Enqueue(queued);
                    }
                }
            }
        }

        public void ClearOutgoingPlayerCommands()
        {
            lock (_outgoingPlayerCommandsLock)
            {
                _outgoingPlayerCommands.Clear();
            }
        }

        public bool QueuePlayerCommandAcknowledgement(
            int targetPeerId,
            int playerId,
            long sequence)
        {
            if (Phase != MatchSessionPhase.Battle ||
                !IsLocalAuthority ||
                sequence <= 0 ||
                !DoesPeerOwnPlayer(targetPeerId, playerId))
            {
                return false;
            }

            lock (_outgoingCommandAcknowledgementsLock)
            {
                if (_outgoingCommandAcknowledgements.Count >= MaxOutgoingCommandAcknowledgements)
                {
                    return false;
                }

                _outgoingCommandAcknowledgements.Enqueue((targetPeerId, playerId, sequence));
                return true;
            }
        }

        public bool TryDequeuePlayerCommandAcknowledgement(
            out int targetPeerId,
            out int playerId,
            out long sequence)
        {
            lock (_outgoingCommandAcknowledgementsLock)
            {
                if (_outgoingCommandAcknowledgements.Count == 0)
                {
                    targetPeerId = 0;
                    playerId = UnownedPlayerId;
                    sequence = 0;
                    return false;
                }

                (int TargetPeerId, int PlayerId, long Sequence) acknowledgement =
                    _outgoingCommandAcknowledgements.Dequeue();
                targetPeerId = acknowledgement.TargetPeerId;
                playerId = acknowledgement.PlayerId;
                sequence = acknowledgement.Sequence;
                return true;
            }
        }

        public void ClearOutgoingCommandAcknowledgements()
        {
            lock (_outgoingCommandAcknowledgementsLock)
            {
                _outgoingCommandAcknowledgements.Clear();
            }
        }

        public long GetLastAcceptedPlayerCommandSequence(int playerId)
        {
            return _lastAcceptedPlayerCommandSequences.TryGetValue(playerId, out long sequence)
                ? sequence
                : 0;
        }

        public bool TryAcceptPlayerCommandSequence(int playerId, long sequence)
        {
            if (Phase != MatchSessionPhase.Battle || !HasPlayer(playerId) || sequence <= 0)
            {
                return false;
            }

            _lastAcceptedPlayerCommandSequences.TryGetValue(playerId, out long lastAccepted);
            if (sequence != lastAccepted + 1)
            {
                return false;
            }

            _lastAcceptedPlayerCommandSequences[playerId] = sequence;
            return true;
        }

        private void RemoveSquadAssignmentsForPlayer(int playerId, int? retainedSide)
        {
            List<Guid> tokensToRemove = new List<Guid>();
            foreach (KeyValuePair<Guid, (int PlayerId, int Side, SavedSquad Squad)> assignment in _squadOwnerAssignments)
            {
                if (assignment.Value.PlayerId == playerId &&
                    (!retainedSide.HasValue || assignment.Value.Side != retainedSide.Value))
                {
                    tokensToRemove.Add(assignment.Key);
                }
            }
            for (int i = 0; i < tokensToRemove.Count; i++)
            {
                _squadOwnerAssignments.Remove(tokensToRemove[i]);
            }
        }

        public bool TryAssignSavedSquadOwner(SavedSquad savedSquad, int playerId)
        {
            if (!IsConfiguring || savedSquad == null)
            {
                return false;
            }

            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            if (player == null || player.Side != savedSquad.Side)
            {
                return false;
            }

            Guid token = savedSquad.MatchOwnershipToken;
            if (token == Guid.Empty || !_squadOwnerAssignments.ContainsKey(token))
            {
                token = Guid.NewGuid();
                savedSquad.MatchOwnershipToken = token;
            }

            _squadOwnerAssignments[token] = (playerId, savedSquad.Side, savedSquad);
            return true;
        }

        public IReadOnlyList<SavedSquad> GetLobbyAssignedSquads()
        {
            return _squadOwnerAssignments
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.Squad)
                .Where(squad => squad != null)
                .ToList();
        }

        public int ResolveSquadOwner(SavedSquad savedSquad, int side)
        {
            if (savedSquad != null &&
                savedSquad.MatchOwnershipToken != Guid.Empty &&
                _squadOwnerAssignments.TryGetValue(
                    savedSquad.MatchOwnershipToken,
                    out (int PlayerId, int Side, SavedSquad Squad) assignment))
            {
                MatchPlayer assignedPlayer = _players.FirstOrDefault(candidate => candidate.Id == assignment.PlayerId);
                return assignment.Side == side &&
                       assignedPlayer != null &&
                       assignedPlayer.Side == side
                    ? assignment.PlayerId
                    : UnownedPlayerId;
            }

            return GetSolePlayerIdForSide(side);
        }

        public bool TryAssignSquadOwner(Squad squad, int playerId)
        {
            if (squad == null)
            {
                return false;
            }

            MatchPlayer player = _players.FirstOrDefault(candidate => candidate.Id == playerId);
            if (player == null || player.Side != squad.Side)
            {
                return false;
            }

            squad.SetOwnerPlayerId(playerId);
            return true;
        }
    }
}
