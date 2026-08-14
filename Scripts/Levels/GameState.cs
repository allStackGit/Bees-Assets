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
        public HashSet<MapObject> PlayerVisibleMapObjects = new HashSet<MapObject>(ReferenceIdentityComparer<MapObject>.Instance);

        public int UserCommands, AICommands;
        public bool IsPaused;
        public bool GameOver;
        public bool LevelEnded;
        private bool _hasEliminationSnapshot;
        private readonly bool[] _eliminationSnapshot = new bool[2];
        public int[] InitialTsv = { 0, 0 };
        public List<SpottedShip>[] SpottedShips = { new List<SpottedShip>(), new List<SpottedShip>() };
        public int[] OriginalSquadCounts = { 0, 0 };
        public Level Level;
        public Stage Stage;
        public HashSet<Ship>[] VisionCache =
        {
            new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance),
            new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance)
        };
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
        public int PlayerNewShipsReceived;
        public int PlayerScore;
        public int PlayerMineralsReceived;

        public List<string> __Squads, __SquadsAwaitingCommands, __PastCommands, __Obstacles;

        public void UpdateDebugVariables()
        {
            __Squads = GetAllSquads().Select(squad => squad.ToString()).ToList();
            __SquadsAwaitingCommands = SquadsAwaitingCommands.Select(squad => squad.ToString()).ToList();
            __PastCommands = PastCommands.Select(command =>
                $"Command #{command.OutcomeId} - {command.CommandType} against {command.Enemy} ended with {command.Tsv}" +
                $" TSV due to \"{command.FinalizationCause}\" and took {command.Age} ticks").ToList();
            __Obstacles = Obstacles.Select(obstacle =>
                $"{obstacle.Name} at {obstacle.GetPosition()} with {obstacle.Health} health").ToList();
        }

        public void Setup(Level level)
        {
            Level = level;
            Stage = Level.Stage;
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
            SquadsAwaitingCommands.Clear();
            PastCommands.Clear();
            OutcomeIdToPastCommandIndex.Clear();
            SelectedSquads.Clear();
            PlayerVisibleMapObjects.Clear();
            Obstacles.Clear();
            FogOfWarVisions.Clear();
            for (int side = 0; side < 2; side++)
            {
                if (SpottedShips[side] == null) SpottedShips[side] = new List<SpottedShip>();
                else SpottedShips[side].Clear();
                InitialTsv[side] = 0;
                OriginalSquadCounts[side] = 0;
                if (HivemindShips[side] == null) HivemindShips[side] = new Dictionary<long, HashSet<Ship>>();
                else HivemindShips[side].Clear();
                if (VisionCache[side] == null) VisionCache[side] = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
                else VisionCache[side].Clear();
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
}
