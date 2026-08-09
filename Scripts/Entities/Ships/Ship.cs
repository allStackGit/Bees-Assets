using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.Rendering;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship : Entity
    {
        public bool ShowDebug;
        public int Health, MaxHealth, OriginalHealth, OriginalTsv, Sight, Clearance, MaxRange, HalfMaxRange;
        public float SizeClass, ProjectileValue, Speed, SpecialFirePower, CurrentSpeed, LongestSide;
        public GameObject ShipExplosion, HealthBar, MiniMapIcon, ShipAnimation, MovementMarker;
        public Vector2 TargetCoordinates, FinalDestination, OffsetFromCenter, PathfindingDestination;
        public Vector2 WaitingTargetCoordinates;
        public Squad Squad, MotherSquad;
        public float DefaultAngle, TargetDirection;
        public int LastKilled;
        public FleetShip FleetShip;
        public string Name;
        public ConfigData.ShipTypes ShipType;
        public HiveMindVision HiveMindVision;
        public FogOfWarVision FogOfWarVision;
        public ProximityCollider ProximityCollider;
        public SortingGroup SortingGroup;

        public bool IsDead;
        public bool IsUserControlled;
        public bool IsMobile;
        public bool HasBrain, IsHiveMindControlled, IsMinionShip, HasTargetCoordinates, IsMiningShip, IsWarpGate, IsBeehive,
            HasTargetDirection, HasUserFogOfWarVision, HasProximityCollider, HasShipAnimation, HasRocketFlares,
            HasLeftRocketFlares, HasCenterRocketFlares, HasRightRocketFlares, HasOnlySideRocketFlares, HasMovementMarker,
            HasWaitingTargetCoordinates, HasRemainsShip, FireAtFrontOfShip, IsBomber;
        public bool IsSpawnedShip;
        public bool HasEnteredMap, AreRocketFlaresOutOfSync, InCombat, IsFollowingPath, CannotChangeMovementOrders;

        public List<Weapon> Weapons;
        public List<GameObject> ColoredPrefabs;
        public List<GameObject> OriginalColoredPrefabs;
        public List<Sprite> OriginalSprites;
        public List<GameObject> WeaponPrefabs, LeftRocketFlares, CenterRocketFlares, RightRocketFlares;
        public Brain Brain;
        public Queue<Vector2> DestinationQueue = new Queue<Vector2>();
        public List<CollisionAsteroid> NearbyAsteroids = new List<CollisionAsteroid>();
        public List<Turret> Turrets = new List<Turret>();
        public ShipAnimationController ShipAnimationController;
        public float RotationSpeed;
        public Ship TargetEnemyShipToFollow;
        public Ship Killer;
        public FleetShip KillerFleetShip;
        public SavedSquad KillerSavedSquad;
        public HashSet<Projectile> ProjectilesInFlight = new HashSet<Projectile>();
        public ShipRemains ShipRemains;
        public HashSet<Weapon> WeaponsThatHaveUsWithinRange = new HashSet<Weapon>();
        public ConfigData.ShipTypeLetters ShipTypeLetter;
        public ConfigData.ShootingStrategyTypes ShootingStrategy;
        public AudioSource ShipExplosionSoundEffect;
        public bool HasShipExplosionSoundEffect;
        public float Firepower;
        public float DamagePerSecond;
        public int Tsv;
        public bool HasWeapons, HasTurrets;
        public bool IsCarrierShip;
        public bool IsMoving;
        public bool IsCeaseFire;
        public bool CanOverrideBounds;
        public HashSet<Ship> ShipsHit = new HashSet<Ship>();

        public volatile bool PathfindingThreadComplete, IsPathfinding;
        public volatile Pathfinder.Path PathfindingValue;
        public volatile int PathfindingRequestId, PathfindingCompletedRequestId;
        public volatile int PathfindingLifecycleId;
        public volatile int PathfindingThread;
        public volatile Pathfinder.Grid DebugGrid;
        public volatile Pathfinder.MapNode[][] DebugNodes;
        public volatile Pathfinder.MapNode DebugEndNode;
        public volatile Pathfinder.MapNode DebugOriginalEndNode;
        public volatile Pathfinder.MapNode DebugStartNode;
        public volatile Pathfinder.MapNode DebugOriginalStartNode;
        public volatile HashSet<Pathfinder.MapNode> DebugWalkablePointNodes = new HashSet<Pathfinder.MapNode>();
        public volatile bool PrintDebugImage;

        public int Direction;
        public bool ShouldDetonate;
        public ConfigData.ShootingStrategyTypes RLShootingStrategy;
        public float RLSide;
        public float RLHealth;
        public float RLShipType;

        private bool _combatTimer, _isInBounds;
        private Transform _healthBarFiller;
        private SpriteRenderer _healthBarFillerSprite;
        private Vector2 _size;
        private float _tempAngle, _tempDistance;
        private int _tempIndex;
        private Obstacle _tempObstacle;
        private Vector2 _tempVelocity;
        private Vector2 _originalMiniMapIconScale;
        private GameObject _tempCollidingThing;

        public bool HasReachedDestination => !HasTargetCoordinates;

        public List<Ship> ShipsWithinRange => HasWeapons
            ? Weapons.Select(weapon => weapon.ShipsWithinRange)
                .Aggregate(new HashSet<Ship>(), (ships, current) =>
                {
                    ships.UnionWith(current.Values);
                    return ships;
                }).ToList()
            : new List<Ship>();

        public bool HasTargetEnemyShipToFollow => TargetEnemyShipToFollow != null && !TargetEnemyShipToFollow.IsDead;
    }
}
