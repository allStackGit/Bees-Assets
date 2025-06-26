using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Pool;

public class Pool : MonoBehaviour
{
    public Stage Stage;
    /// <summary>
    /// The number of every kind of entity/thing in the game, ships, projectiles, asteroids, obstacles, maps, obstacle maps, commands, squads, and so on. 
    /// Used as a unique game-level Id for every created thing
    /// </summary>
    public int ItemCount = 0;

    public ObjectPool<Barge> BargePool;
    public ObjectPool<Beacon> BeaconPool;
    public ObjectPool<Beehive> BeehivePool;
    public ObjectPool<Bumblebee> BumblebeePool;
    public ObjectPool<CarpenterBee> CarpenterBeePool;
    public ObjectPool<Carrier> CarrierPool;
    public ObjectPool<Cruiser> CruiserPool;
    public ObjectPool<Dreadnought> DreadnoughtPool;
    public ObjectPool<Drone> DronePool;
    public ObjectPool<Factory> FactoryPool;
    public ObjectPool<FireBarge> FireBargePool;
    public ObjectPool<Flagship> FlagshipPool;
    public ObjectPool<Frigate> FrigatePool;
    public ObjectPool<Gunship> GunshipPool;
    public ObjectPool<Honeybee> HoneybeePool;
    public ObjectPool<Hornet> HornetPool;
    public ObjectPool<Leafcutter> LeafcutterPool;
    public ObjectPool<Queen> QueenPool;
    public ObjectPool<Scout> ScoutPool;
    public ObjectPool<Striker> StrikerPool;
    public ObjectPool<WarpGate> WarpGatePool;
    public ObjectPool<Wasp> WaspPool;
    public ObjectPool<YellowJacket> YellowJacketPool;


    public ObjectPool<Assets.Scripts.UI_Components.Map> PlutoMapPool;
    public ObjectPool<Assets.Scripts.UI_Components.Map> NeptuneMapPool;
    public ObjectPool<Assets.Scripts.UI_Components.Map> UranusMapPool;

    public ObjectPool<Projectile> BeeSmallProjectilePool;
    public ObjectPool<Projectile> BeeMediumProjectilePool;
    public ObjectPool<Projectile> BumblebeeShotProjectilePool;
    public ObjectPool<Projectile> FlagshipShotProjectilePool;
    public ObjectPool<Projectile> RocketProjectilePool;
    public ObjectPool<Projectile> HumanSmallProjectilePool;
    public ObjectPool<Projectile> HumanMediumProjectilePool;
    public ObjectPool<Projectile> BeamProjectilePool;
    public ObjectPool<Projectile> SplitShotProjectilePool;
    public ObjectPool<Projectile> QueenSmallProjectilePool;
    public ObjectPool<Projectile> QueenLargeProjectilePool;
    public ObjectPool<Projectile> StrikerBombProjectilePool;
    public ObjectPool<Projectile> RocketExplosionProjectilePool;
    public ObjectPool<Projectile> FireBargeExplosionProjectilePool;

    public ObjectPool<ObstacleMap> EmptyObstacleListObjectPool;
    public ObjectPool<ObstacleMap> MazeObstacleListObjectPool;
    public ObjectPool<ObstacleMap> ThreePathsObstacleListObjectPool;
    public ObjectPool<ObstacleMap> ForestObstacleListObjectPool;
    public ObjectPool<ObstacleMap> TheWallObstacleListObjectPool;

    public ObjectPool<CollisionAsteroid> CollisionAsteroidPool;
    /// <summary>
    /// Shards are collision asteroids that have broken off from larger collision asteroids
    /// </summary>
    public ObjectPool<CollisionAsteroid> CollisionAsteroidShardPool;
    /// <summary>
    /// Asteroid pieces have broken off from asteroids but don't actually interact with anything else, they just fade out
    /// </summary>
    public ObjectPool<AsteroidPiece> AsteroidPiecePool;
    public ObjectPool<MiningAsteroid> MiningAsteroidPool;

    public ObjectPool<Squad> SquadPool;
    public ObjectPool<CarrierSquad> CarrierSquadPool;

    public ObjectPool<Aggressive> AggressiveCommandPool;
    public ObjectPool<BombingRun> BombingRunCommandPool;
    public ObjectPool<Charge> ChargeCommandPool;
    public ObjectPool<CircleSquad> CircleSquadCommandPool;
    public ObjectPool<ClosestFriendly> ClosestFriendlyCommandPool;
    public ObjectPool<FullRetreat> FullRetreatCommandPool;
    public ObjectPool<Guard> GuardCommandPool;
    public ObjectPool<InAndOut> InAndOutCommandPool;
    public ObjectPool<Mining> MiningCommandPool;
    public ObjectPool<MoveToRandom> MoveToRandomCommandPool;
    public ObjectPool<Patrol> PatrolCommandPool;
    public ObjectPool<Retreat> RetreatCommandPool;
    public ObjectPool<Scouting> ScoutingCommandPool;
    public ObjectPool<SwipeSquad> SwipeSquadCommandPool;
    public ObjectPool<Hold> HoldCommandPool;
    public ObjectPool<Heal> HealCommandPool;

    // [debug]
    //public List<GameObject> BigCollisionAsteroids;

    public void Setup(Stage stage)
    {
        Stage = stage;

        //BigCollisionAsteroids = Stage.Prefabs.CollisionAsteroidPrefabs.Where((a) => a.GetComponent<CollisionAsteroid>().SizeClass >= 6).ToList();

        BargePool = new ObjectPool<Barge>(CreatePooledBarge, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        BeaconPool = new ObjectPool<Beacon>(CreatePooledBeacon, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        BeehivePool = new ObjectPool<Beehive>(CreatePooledBeehive, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        BumblebeePool = new ObjectPool<Bumblebee>(CreatePooledBumblebee, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        CarpenterBeePool = new ObjectPool<CarpenterBee>(CreatePooledCarpenterBee, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        CarrierPool = new ObjectPool<Carrier>(CreatePooledCarrier, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        CruiserPool = new ObjectPool<Cruiser>(CreatePooledCruiser, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        DreadnoughtPool = new ObjectPool<Dreadnought>(CreatePooledDreadnought, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        DronePool = new ObjectPool<Drone>(CreatePooledDrone, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        FactoryPool = new ObjectPool<Factory>(CreatePooledFactory, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        FireBargePool = new ObjectPool<FireBarge>(CreatePooledFireBarge, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        FlagshipPool = new ObjectPool<Flagship>(CreatePooledFlagship, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        FrigatePool = new ObjectPool<Frigate>(CreatePooledFrigate, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        GunshipPool = new ObjectPool<Gunship>(CreatePooledGunship, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        HoneybeePool = new ObjectPool<Honeybee>(CreatePooledHoneybee, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        HornetPool = new ObjectPool<Hornet>(CreatePooledHornet, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        LeafcutterPool = new ObjectPool<Leafcutter>(CreatePooledLeafcutter, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        QueenPool = new ObjectPool<Queen>(CreatePooledQueen, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        ScoutPool = new ObjectPool<Scout>(CreatePooledScout, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        StrikerPool = new ObjectPool<Striker>(CreatePooledStriker, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        WarpGatePool = new ObjectPool<WarpGate>(CreatePooledWarpGate, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        WaspPool = new ObjectPool<Wasp>(CreatePooledWasp, OnTakeShipFromPool, OnReturnShipToPool, null, true);
        YellowJacketPool = new ObjectPool<YellowJacket>(CreatePooledYellowJacket, OnTakeShipFromPool, OnReturnShipToPool, null, true);

        PlutoMapPool = new ObjectPool<Assets.Scripts.UI_Components.Map>(CreatePooledPlutoMap, null, null, null, true);
        NeptuneMapPool = new ObjectPool<Assets.Scripts.UI_Components.Map>(CreatePooledNeptuneMap, null, null, null, true);
        UranusMapPool = new ObjectPool<Assets.Scripts.UI_Components.Map>(CreatePooledUranusMap, null, null, null, true);

        BeeSmallProjectilePool = new ObjectPool<Projectile>(CreatePooledBeeSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BeeMediumProjectilePool = new ObjectPool<Projectile>(CreatePooledBeeMediumProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BumblebeeShotProjectilePool = new ObjectPool<Projectile>(CreatePooledBumblebeeShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        FlagshipShotProjectilePool = new ObjectPool<Projectile>(CreatePooledFlagshipShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        RocketProjectilePool = new ObjectPool<Projectile>(CreatePooledRocketProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        HumanSmallProjectilePool = new ObjectPool<Projectile>(CreatePooledHumanSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        HumanMediumProjectilePool = new ObjectPool<Projectile>(CreatePooledHumanMediumProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BeamProjectilePool = new ObjectPool<Projectile>(CreatePooledBeamProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        SplitShotProjectilePool = new ObjectPool<Projectile>(CreatePooledSplitShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        QueenSmallProjectilePool = new ObjectPool<Projectile>(CreatePooledQueenSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        QueenLargeProjectilePool = new ObjectPool<Projectile>(CreatePooledQueenLargeProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        StrikerBombProjectilePool = new ObjectPool<Projectile>(CreatePooledStrikerBombProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        RocketExplosionProjectilePool = new ObjectPool<Projectile>(CreatePooledRocketExplosionProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        FireBargeExplosionProjectilePool = new ObjectPool<Projectile>(CreatePooledFireBargeExplosionProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);

        EmptyObstacleListObjectPool = new ObjectPool<ObstacleMap>(CreatePooledEmptyObstacleList, null, null, null, true);
        MazeObstacleListObjectPool = new ObjectPool<ObstacleMap>(CreatePooledMazeObstacleList, null, null, null, true);
        ThreePathsObstacleListObjectPool = new ObjectPool<ObstacleMap>(CreatePooledThreePathsObstacleList, null, null, null, true);
        ForestObstacleListObjectPool = new ObjectPool<ObstacleMap>(CreatePooledForestObstacleList, null, null, null, true);
        TheWallObstacleListObjectPool = new ObjectPool<ObstacleMap>(CreatePooledTheWallObstacleList, null, null, null, true);

        CollisionAsteroidPool = new ObjectPool<CollisionAsteroid>(CreatePooledCollisionAsteroid, null, null, null, true);
        CollisionAsteroidShardPool = new ObjectPool<CollisionAsteroid>(CreatePooledCollisionAsteroidShard, null, null, null, true);
        AsteroidPiecePool = new ObjectPool<AsteroidPiece>(CreatePooledAsteroidPiece, null, null, null, true);
        MiningAsteroidPool = new ObjectPool<MiningAsteroid>(CreatePooledMiningAsteroid, null, null, null, true);

        SquadPool = new ObjectPool<Squad>(CreatePooledSquad, OnTakeSquadFromPool, OnReturnSquadToPool, null, true);
        CarrierSquadPool = new ObjectPool<CarrierSquad>(CreatePooledCarrierSquad, null, null, null, true);

        AggressiveCommandPool = new ObjectPool<Aggressive>(CreatePooledAggressiveCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        BombingRunCommandPool = new ObjectPool<BombingRun>(CreatePooledBombingRunCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        ChargeCommandPool = new ObjectPool<Charge>(CreatePooledChargeCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        CircleSquadCommandPool = new ObjectPool<CircleSquad>(CreatePooledCircleSquadCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        ClosestFriendlyCommandPool = new ObjectPool<ClosestFriendly>(CreatePooledClosestFriendlyCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        FullRetreatCommandPool = new ObjectPool<FullRetreat>(CreatePooledFullRetreatCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        GuardCommandPool = new ObjectPool<Guard>(CreatePooledGuardCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        InAndOutCommandPool = new ObjectPool<InAndOut>(CreatePooledInAndOutCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        MiningCommandPool = new ObjectPool<Mining>(CreatePooledMiningCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        MoveToRandomCommandPool = new ObjectPool<MoveToRandom>(CreatePooledMoveToRandomCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        PatrolCommandPool = new ObjectPool<Patrol>(CreatePooledPatrolCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        RetreatCommandPool = new ObjectPool<Retreat>(CreatePooledRetreatCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        ScoutingCommandPool = new ObjectPool<Scouting>(CreatePooledScoutingCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        SwipeSquadCommandPool = new ObjectPool<SwipeSquad>(CreatePooledSwipeSquadCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        HoldCommandPool = new ObjectPool<Hold>(CreatePooledHoldCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);
        HealCommandPool = new ObjectPool<Heal>(CreatePooledHealCommand, OnTakeCommandFromPool, OnReturnCommandToPool, null, true);

        //FillPools();
    }
    public void OnDestroyPoolObject(MonoBehaviour pooledObject)
    {
        Destroy(pooledObject);
    }
    Aggressive _spawn_aggressive;
    public Aggressive CreatePooledAggressiveCommand()
    {
        _spawn_aggressive = gameObject.AddComponent<Aggressive>();
        _spawn_aggressive.Create(Stage, ConfigData.CommandTypes.Aggressive);
        return _spawn_aggressive;
    }
    BombingRun _spawn_bombingRun;
    public BombingRun CreatePooledBombingRunCommand()
    {
        _spawn_bombingRun = gameObject.AddComponent<BombingRun>();
        _spawn_bombingRun.Create(Stage, ConfigData.CommandTypes.BombingRun);
        return _spawn_bombingRun;
    }
    Charge _spawn_charge;
    public Charge CreatePooledChargeCommand()
    {
        _spawn_charge = gameObject.AddComponent<Charge>();
        _spawn_charge.Create(Stage, ConfigData.CommandTypes.Charge);
        return _spawn_charge;
    }
    CircleSquad _spawn_circleSquad;
    public CircleSquad CreatePooledCircleSquadCommand()
    {
        _spawn_circleSquad = gameObject.AddComponent<CircleSquad>();
        _spawn_circleSquad.Create(Stage, ConfigData.CommandTypes.CircleSquad);
        return _spawn_circleSquad;
    }
    ClosestFriendly _spawn_closestFriendly;
    public ClosestFriendly CreatePooledClosestFriendlyCommand()
    {
        _spawn_closestFriendly = gameObject.AddComponent<ClosestFriendly>();
        _spawn_closestFriendly.Create(Stage, ConfigData.CommandTypes.ClosestFriendly);
        return _spawn_closestFriendly;
    }
    FullRetreat _spawn_fullRetreat;
    public FullRetreat CreatePooledFullRetreatCommand()
    {
        _spawn_fullRetreat = gameObject.AddComponent<FullRetreat>();
        _spawn_fullRetreat.Create(Stage, ConfigData.CommandTypes.FullRetreat);
        return _spawn_fullRetreat;
    }
    Guard _spawn_guard;
    public Guard CreatePooledGuardCommand()
    {
        _spawn_guard = gameObject.AddComponent<Guard>();
        _spawn_guard.Create(Stage, ConfigData.CommandTypes.Guard);
        return _spawn_guard;
    }
    InAndOut _spawn_inAndOut;
    public InAndOut CreatePooledInAndOutCommand()
    {
        _spawn_inAndOut = gameObject.AddComponent<InAndOut>();
        _spawn_inAndOut.Create(Stage, ConfigData.CommandTypes.InAndOut);
        return _spawn_inAndOut;
    }
    Mining _spawn_mining;
    public Mining CreatePooledMiningCommand()
    {
        _spawn_mining = gameObject.AddComponent<Mining>();
        _spawn_mining.Create(Stage, ConfigData.CommandTypes.Mining);
        return _spawn_mining;
    }
    MoveToRandom _spawn_moveToRandom;
    public MoveToRandom CreatePooledMoveToRandomCommand()
    {
        _spawn_moveToRandom = gameObject.AddComponent<MoveToRandom>();
        _spawn_moveToRandom.Create(Stage, ConfigData.CommandTypes.MoveToRandom);
        return _spawn_moveToRandom;
    }
    Patrol _spawn_patrol;
    public Patrol CreatePooledPatrolCommand()
    {
        _spawn_patrol = gameObject.AddComponent<Patrol>();
        _spawn_patrol.Create(Stage, ConfigData.CommandTypes.Patrol);
        return _spawn_patrol;
    }
    Retreat _spawn_retreat;
    public Retreat CreatePooledRetreatCommand()
    {
        _spawn_retreat = gameObject.AddComponent<Retreat>();
        _spawn_retreat.Create(Stage, ConfigData.CommandTypes.Retreat);
        return _spawn_retreat;
    }
    Scouting _spawn_scouting;
    public Scouting CreatePooledScoutingCommand()
    {
        _spawn_scouting = gameObject.AddComponent<Scouting>();
        _spawn_scouting.Create(Stage, ConfigData.CommandTypes.Scouting);
        return _spawn_scouting;
    }
    SwipeSquad _spawn_swipeSquad;
    public SwipeSquad CreatePooledSwipeSquadCommand()
    {
        _spawn_swipeSquad = gameObject.AddComponent<SwipeSquad>();
        _spawn_swipeSquad.Create(Stage, ConfigData.CommandTypes.LeftSwipe);
        return _spawn_swipeSquad;
    }
    private Hold _spawn_hold;
    public Hold CreatePooledHoldCommand()
    {
        _spawn_hold = gameObject.AddComponent<Hold>();
        _spawn_hold.Create(Stage, ConfigData.CommandTypes.Hold);
        return _spawn_hold;
    }
    private Heal _spawn_heal;
    public Heal CreatePooledHealCommand()
    {
        _spawn_heal = gameObject.AddComponent<Heal>();
        _spawn_heal.Create(Stage, ConfigData.CommandTypes.Heal);
        return _spawn_heal;
    }
    CarrierSquad _spawn_carrierSquad;
    public CarrierSquad CreatePooledCarrierSquad()
    {
        _spawn_carrierSquad = gameObject.AddComponent<CarrierSquad>();
        _spawn_carrierSquad.Create(Stage);
        return _spawn_carrierSquad;
    }
    Squad _spawn_squad;
    public Squad CreatePooledSquad()
    {
        //Debug.Log($"Created pooled squad");
        _spawn_squad = gameObject.AddComponent<Squad>();
        _spawn_squad.Create(Stage);
        return _spawn_squad;
    }
    CollisionAsteroid _spawn_collisionAsteroid;
    AsteroidPiece asteroidPiece;
    public CollisionAsteroid CreatePooledCollisionAsteroid()
    {
        // [debug]
        //_spawn_collisionAsteroid = Instantiate(BigCollisionAsteroids[Utilities.RandomInt(BigCollisionAsteroids.Count)]).GetComponent<CollisionAsteroid>();

        _spawn_collisionAsteroid = Instantiate(Stage.Prefabs.CollisionAsteroidPrefabs[Utilities.RandomInt(Stage.Prefabs.CollisionAsteroidPrefabs.Count)]).GetComponent<CollisionAsteroid>();
        _spawn_collisionAsteroid.Create(Stage);
        return _spawn_collisionAsteroid;
    }
    public CollisionAsteroid CreatePooledCollisionAsteroidShard()
    {
        _spawn_collisionAsteroid = Instantiate(Stage.Prefabs.BreakawayAsteroids[Utilities.RandomInt(Stage.Prefabs.BreakawayAsteroids.Count)]).GetComponent<CollisionAsteroid>();
        _spawn_collisionAsteroid.Create(Stage);
        return _spawn_collisionAsteroid;
    }
    public AsteroidPiece CreatePooledAsteroidPiece()
    {
        asteroidPiece = Instantiate(Stage.Prefabs.AsteroidPieces[Utilities.RandomInt(Stage.Prefabs.AsteroidPieces.Count)]).GetComponent<AsteroidPiece>();
        asteroidPiece.Create(Stage);
        return asteroidPiece;
    }
    MiningAsteroid _spawn_miningAsteroid;
    public MiningAsteroid CreatePooledMiningAsteroid()
    {
        _spawn_miningAsteroid = Instantiate(Stage.Prefabs.MiningAsteroidPrefabs[Utilities.RandomInt(Stage.Prefabs.MiningAsteroidPrefabs.Count)]).GetComponent<MiningAsteroid>();
        _spawn_miningAsteroid.Create(Stage);
        return _spawn_miningAsteroid;
    }
    public ObstacleMap CreatePooledEmptyObstacleList()
    {
        return CreatePooledObstacleList(0);
    }
    public ObstacleMap CreatePooledMazeObstacleList()
    {
        return CreatePooledObstacleList(1);
    }
    public ObstacleMap CreatePooledThreePathsObstacleList()
    {
        return CreatePooledObstacleList(2);
    }
    public ObstacleMap CreatePooledForestObstacleList()
    {
        return CreatePooledObstacleList(3);
    }
    public ObstacleMap CreatePooledTheWallObstacleList()
    {
        return CreatePooledObstacleList(4);
    }
    ObstacleMap _spawn_obstacleMap;
    public ObstacleMap CreatePooledObstacleList(int index)
    {
        _spawn_obstacleMap = new ObstacleMap(ItemCount++);
        switch (index)
        {
            case 0:
                Stage.Prefabs.EmptyObstacleList.ForEach((prefab) =>
                {
                    _spawn_obstacleMap.Obstacles.Add(Instantiate(prefab).GetComponent<Obstacle>());
                });
                break;

            case 1:
                Stage.Prefabs.MazePrefabs.ForEach((prefab) =>
                {
                    _spawn_obstacleMap.Obstacles.Add(Instantiate(prefab).GetComponent<Obstacle>());
                });
                break;

            case 2:
                Stage.Prefabs.ThreePathsPrefabs.ForEach((prefab) =>
                {
                    _spawn_obstacleMap.Obstacles.Add(Instantiate(prefab).GetComponent<Obstacle>());
                });
                break;

            case 3:
                Stage.Prefabs.ForestPrefabs.ForEach((prefab) =>
                {
                    _spawn_obstacleMap.Obstacles.Add(Instantiate(prefab).GetComponent<Obstacle>());
                });
                break;

            case 4:
                Stage.Prefabs.TheWallPrefabs.ForEach((prefab) =>
                {
                    _spawn_obstacleMap.Obstacles.Add(Instantiate(prefab).GetComponent<Obstacle>());
                });
                break;
            default:
                Debug.LogError($"The chosen obstacle map index does not match an obstacle map");
                break;
        }
        return _spawn_obstacleMap;
    }
    private Barge _spawn_barge;
    private Beacon _spawn_beacon;
    private Beehive _spawn_beehive;
    private Bumblebee _spawn_bumblebee;
    private CarpenterBee _spawn_carpenterBee;
    private Carrier _spawn_carrier;
    private Cruiser _spawn_cruiser;
    private Dreadnought _spawn_dreadnought;
    private Drone _spawn_drone;
    private Factory _spawn_factory;
    private FireBarge _spawn_fireBarge;
    private Flagship _spawn_flagship;
    private Frigate _spawn_frigate;
    private Gunship _spawn_gunship;
    private Honeybee _spawn_honeybee;
    private Hornet _spawn_hornet;
    private Leafcutter _spawn_leafcutter;
    private Queen _spawn_queen;
    private Scout _spawn_scout;
    private Striker _spawn_striker;
    private WarpGate _spawn_warpGate;
    private Wasp _spawn_wasp;
    private YellowJacket _spawn_yellowJacket;

    // Map Fields
    private Assets.Scripts.UI_Components.Map _spawn_map;

    // Projectile Fields
    private Projectile _spawn_beeSmallProjectile;
    private Projectile _spawn_beeMediumProjectile;
    private Projectile _spawn_bumblebeeShotProjectile;
    private Projectile _spawn_flagshipShotProjectile;
    private Projectile _spawn_rocketProjectile;
    private Projectile _spawn_humanSmallProjectile;
    private Projectile _spawn_humanMediumProjectile;
    private Projectile _spawn_beamProjectile;
    private Projectile _spawn_splitShotProjectile;
    private Projectile _spawn_queenSmallProjectile;
    private Projectile _spawn_queenLargeProjectile;
    private Projectile _spawn_strikerBombProjectile;
    private Projectile _spawn_rocketExplosionProjectile;
    private Projectile _spawn_fireBargeExplosionProjectile;

    // Unit Creation Methods
    public Barge CreatePooledBarge()
    {
        _spawn_barge = Instantiate(Stage.Prefabs.BargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Barge>();
        _spawn_barge.Create(Stage);
        return _spawn_barge;
    }

    public Beacon CreatePooledBeacon()
    {
        _spawn_beacon = Instantiate(Stage.Prefabs.BeaconPrefab, Vector2.zero, Quaternion.identity).GetComponent<Beacon>();
        _spawn_beacon.Create(Stage);
        return _spawn_beacon;
    }

    public Beehive CreatePooledBeehive()
    {
        _spawn_beehive = Instantiate(Stage.Prefabs.BeehivePrefab, Vector2.zero, Quaternion.identity).GetComponent<Beehive>();
        _spawn_beehive.Create(Stage);
        return _spawn_beehive;
    }

    public Bumblebee CreatePooledBumblebee()
    {
        _spawn_bumblebee = Instantiate(Stage.Prefabs.BumblebeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Bumblebee>();
        _spawn_bumblebee.Create(Stage);
        return _spawn_bumblebee;
    }

    public CarpenterBee CreatePooledCarpenterBee()
    {
        _spawn_carpenterBee = Instantiate(Stage.Prefabs.CarpenterBeePrefab, Vector2.zero, Quaternion.identity).GetComponent<CarpenterBee>();
        _spawn_carpenterBee.Create(Stage);
        return _spawn_carpenterBee;
    }

    public Carrier CreatePooledCarrier()
    {
        _spawn_carrier = Instantiate(Stage.Prefabs.CarrierPrefab, Vector2.zero, Quaternion.identity).GetComponent<Carrier>();
        _spawn_carrier.Create(Stage);
        return _spawn_carrier;
    }

    public Cruiser CreatePooledCruiser()
    {
        _spawn_cruiser = Instantiate(Stage.Prefabs.CruiserPrefab, Vector2.zero, Quaternion.identity).GetComponent<Cruiser>();
        _spawn_cruiser.Create(Stage);
        return _spawn_cruiser;
    }

    public Dreadnought CreatePooledDreadnought()
    {
        _spawn_dreadnought = Instantiate(Stage.Prefabs.DreadnoughtPrefab, Vector2.zero, Quaternion.identity).GetComponent<Dreadnought>();
        _spawn_dreadnought.Create(Stage);
        return _spawn_dreadnought;
    }

    public Drone CreatePooledDrone()
    {
        _spawn_drone = Instantiate(Stage.Prefabs.DronePrefab, Vector2.zero, Quaternion.identity).GetComponent<Drone>();
        _spawn_drone.Create(Stage);
        return _spawn_drone;
    }

    public Factory CreatePooledFactory()
    {
        _spawn_factory = Instantiate(Stage.Prefabs.FactoryPrefab, Vector2.zero, Quaternion.identity).GetComponent<Factory>();
        _spawn_factory.Create(Stage);
        return _spawn_factory;
    }

    public FireBarge CreatePooledFireBarge()
    {
        _spawn_fireBarge = Instantiate(Stage.Prefabs.FireBargePrefab, Vector2.zero, Quaternion.identity).GetComponent<FireBarge>();
        _spawn_fireBarge.Create(Stage);
        return _spawn_fireBarge;
    }

    public Flagship CreatePooledFlagship()
    {
        _spawn_flagship = Instantiate(Stage.Prefabs.FlagshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Flagship>();
        _spawn_flagship.Create(Stage);
        return _spawn_flagship;
    }

    public Frigate CreatePooledFrigate()
    {
        _spawn_frigate = Instantiate(Stage.Prefabs.FrigatePrefab, Vector2.zero, Quaternion.identity).GetComponent<Frigate>();
        _spawn_frigate.Create(Stage);
        return _spawn_frigate;
    }

    public Gunship CreatePooledGunship()
    {
        _spawn_gunship = Instantiate(Stage.Prefabs.GunshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Gunship>();
        _spawn_gunship.Create(Stage);
        return _spawn_gunship;
    }

    public Honeybee CreatePooledHoneybee()
    {
        _spawn_honeybee = Instantiate(Stage.Prefabs.HoneybeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Honeybee>();
        _spawn_honeybee.Create(Stage);
        return _spawn_honeybee;
    }

    public Hornet CreatePooledHornet()
    {
        _spawn_hornet = Instantiate(Stage.Prefabs.HornetPrefab, Vector2.zero, Quaternion.identity).GetComponent<Hornet>();
        _spawn_hornet.Create(Stage);
        return _spawn_hornet;
    }

    public Leafcutter CreatePooledLeafcutter()
    {
        _spawn_leafcutter = Instantiate(Stage.Prefabs.LeafcutterPrefab, Vector2.zero, Quaternion.identity).GetComponent<Leafcutter>();
        _spawn_leafcutter.Create(Stage);
        return _spawn_leafcutter;
    }

    public Queen CreatePooledQueen()
    {
        _spawn_queen = Instantiate(Stage.Prefabs.QueenPrefab, Vector2.zero, Quaternion.identity).GetComponent<Queen>();
        _spawn_queen.Create(Stage);
        return _spawn_queen;
    }

    public Scout CreatePooledScout()
    {
        _spawn_scout = Instantiate(Stage.Prefabs.ScoutPrefab, Vector2.zero, Quaternion.identity).GetComponent<Scout>();
        _spawn_scout.Create(Stage);
        return _spawn_scout;
    }

    public Striker CreatePooledStriker()
    {
        _spawn_striker = Instantiate(Stage.Prefabs.StrikerPrefab, Vector2.zero, Quaternion.identity).GetComponent<Striker>();
        _spawn_striker.Create(Stage);
        return _spawn_striker;
    }

    public WarpGate CreatePooledWarpGate()
    {
        _spawn_warpGate = Instantiate(Stage.Prefabs.WarpGatePrefab, Vector2.zero, Quaternion.identity).GetComponent<WarpGate>();
        _spawn_warpGate.Create(Stage);
        return _spawn_warpGate;
    }

    public Wasp CreatePooledWasp()
    {
        _spawn_wasp = Instantiate(Stage.Prefabs.WaspPrefab, Vector2.zero, Quaternion.identity).GetComponent<Wasp>();
        _spawn_wasp.Create(Stage);
        return _spawn_wasp;
    }

    public YellowJacket CreatePooledYellowJacket()
    {
        _spawn_yellowJacket = Instantiate(Stage.Prefabs.YellowJacketPrefab, Vector2.zero, Quaternion.identity).GetComponent<YellowJacket>();
        _spawn_yellowJacket.Create(Stage);
        return _spawn_yellowJacket;
    }

    // Map Creation Methods
    public Assets.Scripts.UI_Components.Map CreatePooledPlutoMap()
    {
       return CreatePooledMap(0);
    }

    public Assets.Scripts.UI_Components.Map CreatePooledNeptuneMap()
    {
        return CreatePooledMap(1);
    }
    public Assets.Scripts.UI_Components.Map CreatePooledUranusMap()
    {
        return CreatePooledMap(2);
    }

    public Assets.Scripts.UI_Components.Map CreatePooledMap(int index)
    {
        // Here the map is instantiated locally because the prefab array is indexed.
        _spawn_map = Instantiate(Stage.Prefabs.Maps[index]).GetComponent<Assets.Scripts.UI_Components.Map>();
        _spawn_map.Create(Stage, index, ItemCount++, ConfigData.Maps[index].Name, ConfigData.Maps[index].UserStartingPosition, ConfigData.Maps[index].AIStartingPosition);
        _spawn_map.name = _spawn_map.Name;
        return _spawn_map;
    }

    // Projectile Creation Methods
    public Projectile CreatePooledBeeSmallProjectile()
    {
        _spawn_beeSmallProjectile = Instantiate(Stage.Prefabs.BeeSmallLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_beeSmallProjectile.Create(Stage);
        return _spawn_beeSmallProjectile;
    }

    public Projectile CreatePooledBeeMediumProjectile()
    {
        _spawn_beeMediumProjectile = Instantiate(Stage.Prefabs.BeeMediumLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_beeMediumProjectile.Create(Stage);
        return _spawn_beeMediumProjectile;
    }

    public Projectile CreatePooledBumblebeeShotProjectile()
    {
        _spawn_bumblebeeShotProjectile = Instantiate(Stage.Prefabs.BumblebeeShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_bumblebeeShotProjectile.Create(Stage);
        return _spawn_bumblebeeShotProjectile;
    }

    public Projectile CreatePooledFlagshipShotProjectile()
    {
        _spawn_flagshipShotProjectile = Instantiate(Stage.Prefabs.FlagshipShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_flagshipShotProjectile.Create(Stage);
        return _spawn_flagshipShotProjectile;
    }

    public Projectile CreatePooledRocketProjectile()
    {
        _spawn_rocketProjectile = Instantiate(Stage.Prefabs.RocketPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_rocketProjectile.Create(Stage);
        return _spawn_rocketProjectile;
    }

    public Projectile CreatePooledHumanSmallProjectile()
    {
        _spawn_humanSmallProjectile = Instantiate(Stage.Prefabs.HumanSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_humanSmallProjectile.Create(Stage);
        return _spawn_humanSmallProjectile;
    }

    public Projectile CreatePooledHumanMediumProjectile()
    {
        _spawn_humanMediumProjectile = Instantiate(Stage.Prefabs.HumanMediumPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_humanMediumProjectile.Create(Stage);
        return _spawn_humanMediumProjectile;
    }

    public Projectile CreatePooledBeamProjectile()
    {
        _spawn_beamProjectile = Instantiate(Stage.Prefabs.BeamPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_beamProjectile.Create(Stage);
        return _spawn_beamProjectile;
    }

    public Projectile CreatePooledSplitShotProjectile()
    {
        _spawn_splitShotProjectile = Instantiate(Stage.Prefabs.SplitShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_splitShotProjectile.Create(Stage);
        return _spawn_splitShotProjectile;
    }

    public Projectile CreatePooledQueenSmallProjectile()
    {
        _spawn_queenSmallProjectile = Instantiate(Stage.Prefabs.QueenSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_queenSmallProjectile.Create(Stage);
        return _spawn_queenSmallProjectile;
    }

    public Projectile CreatePooledQueenLargeProjectile()
    {
        _spawn_queenLargeProjectile = Instantiate(Stage.Prefabs.QueenLargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_queenLargeProjectile.Create(Stage);
        return _spawn_queenLargeProjectile;
    }

    public Projectile CreatePooledStrikerBombProjectile()
    {
        _spawn_strikerBombProjectile = Instantiate(Stage.Prefabs.StrikerBombPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_strikerBombProjectile.Create(Stage);
        return _spawn_strikerBombProjectile;
    }

    public Projectile CreatePooledRocketExplosionProjectile()
    {
        _spawn_rocketExplosionProjectile = Instantiate(Stage.Prefabs.RocketExplosionPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_rocketExplosionProjectile.Create(Stage);
        return _spawn_rocketExplosionProjectile;
    }

    public Projectile CreatePooledFireBargeExplosionProjectile()
    {
        _spawn_fireBargeExplosionProjectile = Instantiate(Stage.Prefabs.FireBargeExplosionPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        _spawn_fireBargeExplosionProjectile.Create(Stage);
        return _spawn_fireBargeExplosionProjectile;
    }

    public void OnTakeShipFromPool(Ship ship)
    {
        //Debug.Log($"{ship.Name} was taken from the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void OnReturnShipToPool(Ship ship)
    {
        //Debug.Log($"{ship.Name} was returned to the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void OnTakeProjectileFromPool(Projectile projectile)
    {
        //Debug.Log($"{projectile.Name} was taken from the pool");
    }

    public void OnReturnProjectileToPool(Projectile projectile)
    {
        //Debug.Log($"{projectile.Name} was returned to the pool");
    }
    public CarrierSquad GetCarrierSquadFromPool()
    {
        return CarrierSquadPool.Get();
    }
    public void ReturnSquadToPool(Squad squad)
    {
        //Debug.Log($"Returning squad to pool: {squad}");
        if (squad.SquadType != ConfigData.SquadTypes.CarrierSquad)
        {
            SquadPool.Release(squad);
        }
        else
        {
            CarrierSquadPool.Release((CarrierSquad)squad);
        }
    }
    public void OnReturnSquadToPool(Squad squad)
    {
        //Debug.Log($"Squad was returned to pool: {squad}");
    }
    public void OnTakeSquadFromPool(Squad squad)
    {
        //Debug.Log($"Squad was taken from pool: {squad}");
    }
    public void OnReturnCommandToPool(Command command)
    {
        //Debug.Log($"Command was returned to pool: {command}");
    }
    public void OnTakeCommandFromPool(Command command)
    {
        //Debug.Log($"Command was taken from pool: {command}");
    }
    public Squad GetSquadFromPool()
    {
        //Debug.Log($"Got squad from pool");
        return SquadPool.Get();
    }
    public void ReturnMiningAsteroidToPool(MiningAsteroid asteroid)
    {
        //Debug.Log($"Returning mining asteroid {asteroid.Name} to pool");
        //asteroid.gameObject.SetActive(false);
        MiningAsteroidPool.Release(asteroid);
    }
    public MiningAsteroid GetMiningAsteroidFromPool()
    {
        return MiningAsteroidPool.Get();
    }

    public void ReturnCollisionAsteroidToPool(CollisionAsteroid asteroid)
    {
        //Debug.Log($"Returning collision asteroid {asteroid.Name} to pool");
        //asteroid.gameObject.SetActive(false);
        CollisionAsteroidPool.Release(asteroid);
    }
    public void ReturnCollisionAsteroidShardToPool(CollisionAsteroid asteroid)
    {
        //Debug.Log($"Returning collision asteroid {asteroid.Name} to pool");
        //asteroid.gameObject.SetActive(false);
        CollisionAsteroidShardPool.Release(asteroid);
    }
    public void ReturnAsteroidPieceToPool(AsteroidPiece piece)
    {
        //Debug.Log($"Returning collision asteroid {asteroid.Name} to pool");
        //asteroid.gameObject.SetActive(false);
        AsteroidPiecePool.Release(piece);
    }
    public CollisionAsteroid GetCollisionAsteroidFromPool()
    {
        return CollisionAsteroidPool.Get();
    }
    public CollisionAsteroid GetCollisionAsteroidShardFromPool()
    {
        return CollisionAsteroidShardPool.Get();
    }
    public AsteroidPiece GetAsteroidPieceFromPool()
    {
        return AsteroidPiecePool.Get();
    }
    public void ReturnObstacleMapToPool(ObstacleMap obstacleMap, int index)
    {
        obstacleMap.Obstacles.ForEach(obstacle =>
        {
            obstacle.gameObject.SetActive(false);
        });
        switch (index)
        {
            case 0:
                EmptyObstacleListObjectPool.Release(obstacleMap);
                break;

            case 1:
                MazeObstacleListObjectPool.Release(obstacleMap);
                break;

            case 2:
                ThreePathsObstacleListObjectPool.Release(obstacleMap);
                break;

            case 3:
                ForestObstacleListObjectPool.Release(obstacleMap);
                break;

            case 4:
                TheWallObstacleListObjectPool.Release(obstacleMap);
                break;
            default:
                Debug.LogError($"The chosen obstacle map index does not match an obstacle map");
                break;
        }
    }
    public ObstacleMap GetObstacleMapFromPool(int index)
    {
        ObstacleMap obstacleMap = null;
        switch (index)
        {
            case 0:
                obstacleMap = EmptyObstacleListObjectPool.Get();
                break;

            case 1:
                obstacleMap = MazeObstacleListObjectPool.Get();
                break;

            case 2:
                obstacleMap = ThreePathsObstacleListObjectPool.Get();
                break;

            case 3:
                obstacleMap = ForestObstacleListObjectPool.Get();
                break;

            case 4:
                obstacleMap = TheWallObstacleListObjectPool.Get();
                break;

            default:
                Debug.LogError($"The chosen obstacle map index does not match an obstacle map");
                break;
        }
        obstacleMap.Obstacles.ForEach(obstacle =>
        {
            obstacle.gameObject.SetActive(true);
        });
        return obstacleMap;
    }

    public void ReturnShipToPool(Ship ship)
    {

        switch (ship.ShipType)
        {
            case ConfigData.ShipTypes.Barge:
                BargePool.Release((Barge)ship);
                break;

            case ConfigData.ShipTypes.Beacon:
                BeaconPool.Release((Beacon)ship);
                break;

            case ConfigData.ShipTypes.Beehive:
                BeehivePool.Release((Beehive)ship);
                break;

            case ConfigData.ShipTypes.Bumblebee:
                BumblebeePool.Release((Bumblebee)ship);
                break;

            case ConfigData.ShipTypes.CarpenterBee:
                CarpenterBeePool.Release((CarpenterBee)ship);
                break;

            case ConfigData.ShipTypes.Carrier:
                CarrierPool.Release((Carrier)ship);
                break;

            case ConfigData.ShipTypes.Cruiser:
                CruiserPool.Release((Cruiser)ship);
                break;

            case ConfigData.ShipTypes.Dreadnought:
                DreadnoughtPool.Release((Dreadnought)ship);
                break;

            case ConfigData.ShipTypes.Drone:
                DronePool.Release((Drone)ship);
                break;

            case ConfigData.ShipTypes.Factory:
                FactoryPool.Release((Factory)ship);
                break;

            case ConfigData.ShipTypes.FireBarge:
                FireBargePool.Release((FireBarge)ship);
                break;

            case ConfigData.ShipTypes.Flagship:
                FlagshipPool.Release((Flagship)ship);
                break;

            case ConfigData.ShipTypes.Frigate:
                FrigatePool.Release((Frigate)ship);
                break;

            case ConfigData.ShipTypes.Gunship:
                GunshipPool.Release((Gunship)ship);
                break;

            case ConfigData.ShipTypes.Honeybee:
                HoneybeePool.Release((Honeybee)ship);
                break;

            case ConfigData.ShipTypes.Hornet:
                HornetPool.Release((Hornet)ship);
                break;

            case ConfigData.ShipTypes.Leafcutter:
                LeafcutterPool.Release((Leafcutter)ship);
                break;

            case ConfigData.ShipTypes.Queen:
                QueenPool.Release((Queen)ship);
                break;

            case ConfigData.ShipTypes.Scout:
                ScoutPool.Release((Scout)ship);
                break;

            case ConfigData.ShipTypes.Striker:
                StrikerPool.Release((Striker)ship);
                break;

            case ConfigData.ShipTypes.WarpGate:
                WarpGatePool.Release((WarpGate)ship);
                break;

            case ConfigData.ShipTypes.Wasp:
                WaspPool.Release((Wasp)ship);
                break;

            case ConfigData.ShipTypes.YellowJacket:
                YellowJacketPool.Release((YellowJacket)ship);
                break;

            default:
                Debug.LogError($"The returned ship type {ship.ShipType} does not match a pool");
                break;



        }
    }
    public Assets.Scripts.UI_Components.Map GetPooledMap(int index)
    {
        //Debug.Log($"Getting map from pool");

        switch (index)
        {
            case 0:
                return PlutoMapPool.Get();
            case 1:
                return NeptuneMapPool.Get();
            case 2:
                return UranusMapPool.Get();
            default:
                Debug.LogError($"Map index is invalid: {index}");
                break;
        }
        Debug.LogError($"Invalid map index: {index}");
        return null;
    }
    public void ReturnMapToPool(Assets.Scripts.UI_Components.Map map)
    {
        //Debug.Log($"Returning {map.Name} to pool");
        map.gameObject.SetActive(false);
        switch (map.Index)
        {
            case 0:
                PlutoMapPool.Release(map);
                break;

            case 1:
                NeptuneMapPool.Release(map);
                break;
            case 2:
                UranusMapPool.Release(map);
                break;

            default:
                Debug.LogError($"Map index is invalid: {map.Index}");
                break;
        }
    }

    public Projectile GetProjectileFromPool(ConfigData.ProjectileTypes type)
    {
        switch (type)
        {
            case ConfigData.ProjectileTypes.BeeSmall:
                return BeeSmallProjectilePool.Get();

            case ConfigData.ProjectileTypes.BeeMedium:
                return BeeMediumProjectilePool.Get();

            case ConfigData.ProjectileTypes.BumblebeeShot:
                return BumblebeeShotProjectilePool.Get();

            case ConfigData.ProjectileTypes.FlagshipShot:
                return FlagshipShotProjectilePool.Get();

            case ConfigData.ProjectileTypes.Rocket:
                return RocketProjectilePool.Get();

            case ConfigData.ProjectileTypes.HumanSmall:
                return HumanSmallProjectilePool.Get();

            case ConfigData.ProjectileTypes.HumanMedium:
                return HumanMediumProjectilePool.Get();

            case ConfigData.ProjectileTypes.Beam:
                return BeamProjectilePool.Get();

            case ConfigData.ProjectileTypes.SplitShot:
                return SplitShotProjectilePool.Get();

            case ConfigData.ProjectileTypes.QueenSmall:
                return QueenSmallProjectilePool.Get();

            case ConfigData.ProjectileTypes.QueenLarge:
                return QueenLargeProjectilePool.Get();

            case ConfigData.ProjectileTypes.StrikerBomb:
                return StrikerBombProjectilePool.Get();

            case ConfigData.ProjectileTypes.RocketExplosion:
                return RocketExplosionProjectilePool.Get();

            case ConfigData.ProjectileTypes.FireBargeExplosion:
                return FireBargeExplosionProjectilePool.Get();

            default:
                Debug.LogError($"Projectile type is invalid: {type}");
                return null;
        }
    }
    public void ReturnProjectileToPool(Projectile projectile)
    {
        //projectile.gameObject.SetActive(false);
        switch (projectile.Type)
        {
            case ConfigData.ProjectileTypes.BeeSmall:
                BeeSmallProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.BeeMedium:
                BeeMediumProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.BumblebeeShot:
                BumblebeeShotProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.FlagshipShot:
                FlagshipShotProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.Rocket:
                RocketProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.HumanSmall:
                HumanSmallProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.HumanMedium:
                HumanMediumProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.Beam:
                BeamProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.SplitShot:
                SplitShotProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.QueenSmall:
                QueenSmallProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.QueenLarge:
                QueenLargeProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.StrikerBomb:
                StrikerBombProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.RocketExplosion:
                RocketExplosionProjectilePool.Release(projectile);
                break;
            case ConfigData.ProjectileTypes.FireBargeExplosion:
                FireBargeExplosionProjectilePool.Release(projectile);
                break;
            default:
                Debug.LogError($"Projectile type is invalid: {projectile}");
                break;
        }
    }

    public void ReturnCommandToPool(Command command)
    {
        //Debug.Log($"Returning command {command.CommandType} to pool");
        switch (command.CommandType)
        {
            case ConfigData.CommandTypes.Aggressive:
                AggressiveCommandPool.Release((Aggressive)command);
                break;
            case ConfigData.CommandTypes.BombingRun:
                BombingRunCommandPool.Release((BombingRun)command);
                break;
            case ConfigData.CommandTypes.Charge:
                ChargeCommandPool.Release((Charge)command);
                break;
            case ConfigData.CommandTypes.CircleSquad:
                CircleSquadCommandPool.Release((CircleSquad)command);
                break;
            case ConfigData.CommandTypes.ClosestFriendly:
                ClosestFriendlyCommandPool.Release((ClosestFriendly)command);
                break;
            case ConfigData.CommandTypes.FullRetreat:
                FullRetreatCommandPool.Release((FullRetreat)command);
                break;
            case ConfigData.CommandTypes.Guard:
                GuardCommandPool.Release((Guard)command);
                break;
            case ConfigData.CommandTypes.InAndOut:
                InAndOutCommandPool.Release((InAndOut)command);
                break;
            case ConfigData.CommandTypes.Mining:
                MiningCommandPool.Release((Mining)command);
                break;
            case ConfigData.CommandTypes.MoveToRandom:
                MoveToRandomCommandPool.Release((MoveToRandom)command);
                break;
            case ConfigData.CommandTypes.Patrol:
                PatrolCommandPool.Release((Patrol)command);
                break;
            case ConfigData.CommandTypes.Retreat:
                RetreatCommandPool.Release((Retreat)command);
                break;
            case ConfigData.CommandTypes.Scouting:
                ScoutingCommandPool.Release((Scouting)command);
                break;
            case ConfigData.CommandTypes.LeftSwipe:
            case ConfigData.CommandTypes.RightSwipe:
                SwipeSquadCommandPool.Release((SwipeSquad)command);
                break;
            case ConfigData.CommandTypes.Hold:
                HoldCommandPool.Release((Hold)command);
                break;
            case ConfigData.CommandTypes.Heal:
                HealCommandPool.Release((Heal)command);
                break;
            default:
                Debug.LogError($"Command type is invalid: {command.CommandType}");
                break;
        }
    }
    public Command GetCommandFromPool(ConfigData.CommandTypes type)
    {
        //Debug.Log($"Getting command {type} from pool");
        switch (type)
        {
            case ConfigData.CommandTypes.Aggressive:
                return AggressiveCommandPool.Get();
            case ConfigData.CommandTypes.BombingRun:
                return BombingRunCommandPool.Get();
            case ConfigData.CommandTypes.Charge:
                return ChargeCommandPool.Get();
            case ConfigData.CommandTypes.CircleSquad:
                return CircleSquadCommandPool.Get();
            case ConfigData.CommandTypes.ClosestFriendly:
                return ClosestFriendlyCommandPool.Get();
            case ConfigData.CommandTypes.FullRetreat:
                return FullRetreatCommandPool.Get();
            case ConfigData.CommandTypes.Guard:
                return GuardCommandPool.Get();
            case ConfigData.CommandTypes.InAndOut:
                return InAndOutCommandPool.Get();
            case ConfigData.CommandTypes.Mining:
                return MiningCommandPool.Get();
            case ConfigData.CommandTypes.MoveToRandom:
                return MoveToRandomCommandPool.Get();
            case ConfigData.CommandTypes.Patrol:
                return PatrolCommandPool.Get();
            case ConfigData.CommandTypes.Retreat:
                return RetreatCommandPool.Get();
            case ConfigData.CommandTypes.Scouting:
                return ScoutingCommandPool.Get();
            case ConfigData.CommandTypes.LeftSwipe:
            case ConfigData.CommandTypes.RightSwipe:
                return SwipeSquadCommandPool.Get();
            case ConfigData.CommandTypes.Hold:
                return HoldCommandPool.Get();
            case ConfigData.CommandTypes.Heal:
                return HealCommandPool.Get();
            default:
                Debug.LogError($"Command type is invalid: {type}");
                return null;
        }
    }
    // Class-level fields for FillPools
    private int _fillPool_fillSizeSmall;
    private int _fillPool_fillSizeMedium;
    private int _fillPool_fillSizeLarge;
    private int _fillPool_i;
    private List<Ship> _fillPool_spawnedShips = new List<Ship>();
    private List<Projectile> _fillPool_spawnedProjectiles = new List<Projectile>();
    private List<Command> _fillPool_spawnedCommands = new List<Command>();
    private List<Squad> _fillPool_spawnedSquads = new List<Squad>();

    private void FillPools()
    {
        // Calculate fill sizes based on Levels
        _fillPool_fillSizeSmall = 15 * Stage.LevelCount / 2;
        _fillPool_fillSizeMedium = 10 * Stage.LevelCount / 2;
        _fillPool_fillSizeLarge = 5 * Stage.LevelCount / 2;

        //_fillPool_fillSizeSmall = 70;
        //_fillPool_fillSizeMedium = 70;
        //_fillPool_fillSizeLarge = 70;

        // Fill small pool items
        for (_fillPool_i = 0; _fillPool_i < _fillPool_fillSizeSmall; _fillPool_i++)
        {
            _fillPool_spawnedShips.Add(BeaconPool.Get());
            _fillPool_spawnedShips.Add(DronePool.Get());
            _fillPool_spawnedShips.Add(HoneybeePool.Get());
            _fillPool_spawnedShips.Add(HornetPool.Get());
            _fillPool_spawnedShips.Add(ScoutPool.Get());
            _fillPool_spawnedShips.Add(StrikerPool.Get());
            _fillPool_spawnedShips.Add(YellowJacketPool.Get());

            _fillPool_spawnedProjectiles.Add(BeeSmallProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(HumanSmallProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(QueenSmallProjectilePool.Get());

            _fillPool_spawnedSquads.Add(SquadPool.Get());
        }

        // Fill medium pool items
        for (_fillPool_i = 0; _fillPool_i < _fillPool_fillSizeMedium; _fillPool_i++)
        {
            _fillPool_spawnedShips.Add(BargePool.Get());
            _fillPool_spawnedShips.Add(BumblebeePool.Get());
            _fillPool_spawnedShips.Add(CarpenterBeePool.Get());
            _fillPool_spawnedShips.Add(CarrierPool.Get());
            _fillPool_spawnedShips.Add(CruiserPool.Get());
            _fillPool_spawnedShips.Add(DreadnoughtPool.Get());
            _fillPool_spawnedShips.Add(FrigatePool.Get());
            _fillPool_spawnedShips.Add(GunshipPool.Get());
            _fillPool_spawnedShips.Add(LeafcutterPool.Get());
            _fillPool_spawnedShips.Add(WaspPool.Get());

            _fillPool_spawnedProjectiles.Add(BeeMediumProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(RocketProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(HumanMediumProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(SplitShotProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(StrikerBombProjectilePool.Get());
             _fillPool_spawnedProjectiles.Add(RocketExplosionProjectilePool.Get());

            _fillPool_spawnedCommands.Add(AggressiveCommandPool.Get());
            _fillPool_spawnedCommands.Add(BombingRunCommandPool.Get());
            _fillPool_spawnedCommands.Add(ChargeCommandPool.Get());
            _fillPool_spawnedCommands.Add(CircleSquadCommandPool.Get());
            _fillPool_spawnedCommands.Add(ClosestFriendlyCommandPool.Get());
            _fillPool_spawnedCommands.Add(FullRetreatCommandPool.Get());
            _fillPool_spawnedCommands.Add(GuardCommandPool.Get());
            _fillPool_spawnedCommands.Add(InAndOutCommandPool.Get());
            _fillPool_spawnedCommands.Add(MiningCommandPool.Get());
            _fillPool_spawnedCommands.Add(MoveToRandomCommandPool.Get());
            _fillPool_spawnedCommands.Add(PatrolCommandPool.Get());
            _fillPool_spawnedCommands.Add(RetreatCommandPool.Get());
            _fillPool_spawnedCommands.Add(ScoutingCommandPool.Get());
            _fillPool_spawnedCommands.Add(SwipeSquadCommandPool.Get());

            _fillPool_spawnedSquads.Add(CarrierSquadPool.Get());

        }

        // Fill large pool items
        for (_fillPool_i = 0; _fillPool_i < _fillPool_fillSizeLarge; _fillPool_i++)
        {
            _fillPool_spawnedShips.Add(BeehivePool.Get());
            _fillPool_spawnedShips.Add(FactoryPool.Get());
            _fillPool_spawnedShips.Add(FireBargePool.Get());
            _fillPool_spawnedShips.Add(FlagshipPool.Get());
            _fillPool_spawnedShips.Add(QueenPool.Get());
            _fillPool_spawnedShips.Add(WarpGatePool.Get());

            _fillPool_spawnedProjectiles.Add(BumblebeeShotProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(FlagshipShotProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(BeamProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(QueenLargeProjectilePool.Get());
            _fillPool_spawnedProjectiles.Add(FireBargeExplosionProjectilePool.Get());
        }

        // Process and return items to their respective pools
        _fillPool_spawnedShips.ForEach((ship) =>
        {
            ReturnShipToPool(ship);
        });

        _fillPool_spawnedProjectiles.ForEach((projectile) =>
        {
            ReturnProjectileToPool(projectile);
        });

        _fillPool_spawnedCommands.ForEach((command) =>
        {
            ReturnCommandToPool(command);
        });

        _fillPool_spawnedSquads.ForEach((squad) =>
        {
            ReturnSquadToPool(squad);
        });
    }

    // Class-level field for the asteroid pool
    private List<CollisionAsteroid> _fillPool_spawnedAsteroids = new List<CollisionAsteroid>();

    public void FillAsteroidPool()
    {
        for (int _fillPool_i = 0; _fillPool_i < _fillPool_fillSizeSmall; _fillPool_i++)
        {
            _fillPool_spawnedAsteroids.Add(CollisionAsteroidPool.Get());
        }
        _fillPool_spawnedAsteroids.ForEach((spawnedAsteroid) =>
        {
            ReturnCollisionAsteroidToPool(spawnedAsteroid);
        });
    }
}
