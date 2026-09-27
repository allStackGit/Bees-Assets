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
    public sealed class MatchPlayer
    {
        public int Id { get; }
        public int Side { get; private set; }
        public bool IsLocal { get; }

        public MatchPlayer(int id, int side, bool isLocal)
        {
            Id = id;
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

    /// <summary>
    /// Transient Free Play participant/ownership state. Campaign and Challenge do not create
    /// this session; their existing single-player ownership path remains unchanged.
    /// </summary>
    public sealed class MatchSession
    {
        public const int UnownedPlayerId = 0;
        public const int LegacyLocalPlayerId = 1;

        private readonly List<MatchPlayer> _players = new List<MatchPlayer>();
        private long _nextMatchSquadId = 1;
        private readonly Dictionary<int, long> _nextPlayerCommandSequences = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _lastAcceptedPlayerCommandSequences = new Dictionary<int, long>();
        private readonly List<(SavedSquad Squad, int PlayerId)> _squadOwnerAssignments =
            new List<(SavedSquad Squad, int PlayerId)>();

        public IReadOnlyList<MatchPlayer> Players => _players;
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

        public bool AddPlayer(int playerId, int side, bool isLocal)
        {
            if (!IsConfiguring || playerId <= UnownedPlayerId)
            {
                return false;
            }
            if (side != ConfigData.Configuration.BeeSide && side != ConfigData.Configuration.HumanSide)
            {
                return false;
            }
            if (_players.Any(player => player.Id == playerId))
            {
                return false;
            }

            _players.Add(new MatchPlayer(playerId, side, isLocal));
            if (isLocal && PrimaryLocalPlayerId == UnownedPlayerId)
            {
                PrimaryLocalPlayerId = playerId;
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
            _squadOwnerAssignments.RemoveAll(assignment => assignment.PlayerId == playerId);
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
            _squadOwnerAssignments.RemoveAll(assignment =>
                assignment.PlayerId == playerId &&
                (assignment.Squad == null || assignment.Squad.Side != side));
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

        public bool TryBeginBattle()
        {
            if (!IsConfiguring || _players.Count == 0 || PrimaryLocalPlayerId == UnownedPlayerId)
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

        public bool TryAcceptPlayerCommandSequence(int playerId, long sequence)
        {
            if (Phase != MatchSessionPhase.Battle || !HasPlayer(playerId) || sequence <= 0)
            {
                return false;
            }

            _lastAcceptedPlayerCommandSequences.TryGetValue(playerId, out long lastAccepted);
            if (sequence <= lastAccepted)
            {
                return false;
            }

            _lastAcceptedPlayerCommandSequences[playerId] = sequence;
            return true;
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

            for (int i = 0; i < _squadOwnerAssignments.Count; i++)
            {
                if (ReferenceEquals(_squadOwnerAssignments[i].Squad, savedSquad))
                {
                    _squadOwnerAssignments[i] = (savedSquad, playerId);
                    return true;
                }
            }

            _squadOwnerAssignments.Add((savedSquad, playerId));
            return true;
        }

        public int ResolveSquadOwner(SavedSquad savedSquad, int side)
        {
            if (savedSquad != null)
            {
                for (int i = 0; i < _squadOwnerAssignments.Count; i++)
                {
                    (SavedSquad Squad, int PlayerId) assignment = _squadOwnerAssignments[i];
                    if (!ReferenceEquals(assignment.Squad, savedSquad))
                    {
                        continue;
                    }

                    MatchPlayer assignedPlayer = _players.FirstOrDefault(candidate => candidate.Id == assignment.PlayerId);
                    return assignedPlayer != null && assignedPlayer.Side == side
                        ? assignment.PlayerId
                        : UnownedPlayerId;
                }

                // LevelOptions can clone SavedSquad instances before spawning. Preserve the
                // match assignment across that clone when the persistent/transient squad id is
                // unambiguous on this side. If two different players own the same id, refuse to
                // guess; a later network loadout layer will supply a dedicated match-squad id.
                int fallbackPlayerId = UnownedPlayerId;
                for (int i = 0; i < _squadOwnerAssignments.Count; i++)
                {
                    (SavedSquad Squad, int PlayerId) assignment = _squadOwnerAssignments[i];
                    if (assignment.Squad == null ||
                        assignment.Squad.Side != side ||
                        assignment.Squad.Id != savedSquad.Id)
                    {
                        continue;
                    }

                    if (fallbackPlayerId == UnownedPlayerId)
                    {
                        fallbackPlayerId = assignment.PlayerId;
                    }
                    else if (fallbackPlayerId != assignment.PlayerId)
                    {
                        return UnownedPlayerId;
                    }
                }

                if (fallbackPlayerId != UnownedPlayerId)
                {
                    MatchPlayer fallbackPlayer = _players.FirstOrDefault(candidate => candidate.Id == fallbackPlayerId);
                    return fallbackPlayer != null && fallbackPlayer.Side == side
                        ? fallbackPlayerId
                        : UnownedPlayerId;
                }
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
