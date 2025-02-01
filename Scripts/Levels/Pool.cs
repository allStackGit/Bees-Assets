using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

public class Pool : MonoBehaviour
{
    public Stage Stage;
    /// <summary>
    /// The number of every kind of entity/thing in the game, ships, projectiles, asteroids, obstacles, maps, obstacle maps, commands, squads, and so on
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



    public int __BargePoolSize, __BeaconPoolSize, __BeehivePoolSize, __BumblebeePoolSize, __CarpenterBeePoolSize, __CarrierPoolSize, __CruiserPoolSize, __DreadnoughtPoolSize,
        __DronePoolSize, __FactoryPoolSize, __FireBargePoolSize, __FlagshipPoolSize, __FrigatePoolSize, __GunshipPoolSize, __HoneybeePoolSize, __HornetPoolSize, __LeafcutterPoolSize,
        __QueenPoolSize, __ScoutPoolSize, __StrikerPoolSize, __WarpGatePoolSize, __WaspPoolSize, __YellowJacketPoolSize, __PlutoMapPoolSize, __UranusMapPoolSize, __BeeSmallProjectilePoolSize,
        __BeeMediumProjectilePoolSize, __BumblebeeShotProjectilePoolSize, __FlagshipShotProjectilePoolSize, __RocketProjectilePoolSize, __HumanSmallProjectilePoolSize, __HumanMediumProjectilePoolSize,
        __BeamProjectilePoolSize, __SplitShotProjectilePoolSize, __QueenSmallProjectilePoolSize, __QueenLargeProjectilePoolSize, __StrikerBombProjectilePoolSize, __RocketExplosionProjectilePoolSize,
        __FireBargeExplosionProjectilePoolSize, __EmptyObstacleListObjectPoolSize, __MazeObstacleListObjectPoolSize, __ThreePathsObstacleListObjectPoolSize, __ForestObstacleListObjectPoolSize,
        __TheWallObstacleListObjectPoolSize, __CollisionAsteroidPoolSize, __MiningAsteroidPoolSize, __SquadPoolSize, __CarrierSquadPoolSize, __AggressiveCommandPoolSize, __BombingRunCommandPoolSize,
        __ChargeCommandPoolSize, __CircleSquadCommandPoolSize, __ClosestFriendlyCommandPoolSize, __FullRetreatCommandPoolSize, __GuardCommandPoolSize, __InAndOutCommandPoolSize,
        __MiningCommandPoolSize, __MoveToRandomCommandPoolSize, __PatrolCommandPoolSize, __RetreatCommandPoolSize, __ScoutingCommandPoolSize, __SwipeSquadCommandPoolSize;
    public void DebugLogger()
    {
        __BargePoolSize = BargePool.CountAll;
        __BeaconPoolSize = BeaconPool.CountAll;
        __BeehivePoolSize = BeehivePool.CountAll;
        __BumblebeePoolSize = BumblebeePool.CountAll;
        __CarpenterBeePoolSize = CarpenterBeePool.CountAll;
        __CarrierPoolSize = CarrierPool.CountAll;
        __CruiserPoolSize = CruiserPool.CountAll;
        __DreadnoughtPoolSize = DreadnoughtPool.CountAll;
        __DronePoolSize = DronePool.CountAll;
        __FactoryPoolSize = FactoryPool.CountAll;
        __FireBargePoolSize = FireBargePool.CountAll;
        __FlagshipPoolSize = FlagshipPool.CountAll;
        __FrigatePoolSize = FrigatePool.CountAll;
        __GunshipPoolSize = GunshipPool.CountAll;
        __HoneybeePoolSize = HoneybeePool.CountAll;
        __HornetPoolSize = HornetPool.CountAll;
        __LeafcutterPoolSize = LeafcutterPool.CountAll;
        __QueenPoolSize = QueenPool.CountAll;
        __ScoutPoolSize = ScoutPool.CountAll;
        __StrikerPoolSize = StrikerPool.CountAll;
        __WarpGatePoolSize = WarpGatePool.CountAll;
        __WaspPoolSize = WarpGatePool.CountAll;
        __YellowJacketPoolSize = YellowJacketPool.CountAll;
        __PlutoMapPoolSize = PlutoMapPool.CountAll;
        __UranusMapPoolSize = UranusMapPool.CountAll;
        __BeeSmallProjectilePoolSize = BeeSmallProjectilePool.CountAll;
        __BeeMediumProjectilePoolSize = BeeMediumProjectilePool.CountAll;
        __BumblebeeShotProjectilePoolSize = BumblebeeShotProjectilePool.CountAll;
        __FlagshipShotProjectilePoolSize = FlagshipShotProjectilePool.CountAll;
        __RocketProjectilePoolSize = RocketProjectilePool.CountAll;
        __HumanSmallProjectilePoolSize = HumanSmallProjectilePool.CountAll;
        __HumanMediumProjectilePoolSize = HumanMediumProjectilePool.CountAll;
        __BeamProjectilePoolSize = BeamProjectilePool.CountAll;
        __SplitShotProjectilePoolSize = SplitShotProjectilePool.CountAll;
        __QueenSmallProjectilePoolSize = QueenSmallProjectilePool.CountAll;
        __QueenLargeProjectilePoolSize = QueenLargeProjectilePool.CountAll;
        __StrikerBombProjectilePoolSize = StrikerBombProjectilePool.CountAll;
        __RocketExplosionProjectilePoolSize = RocketExplosionProjectilePool.CountAll;
        __FireBargeExplosionProjectilePoolSize = FireBargeExplosionProjectilePool.CountAll;
        __EmptyObstacleListObjectPoolSize = EmptyObstacleListObjectPool.CountAll;
        __MazeObstacleListObjectPoolSize = MazeObstacleListObjectPool.CountAll;
        __ThreePathsObstacleListObjectPoolSize = ThreePathsObstacleListObjectPool.CountAll;
        __ForestObstacleListObjectPoolSize = ForestObstacleListObjectPool.CountAll;
        __TheWallObstacleListObjectPoolSize = TheWallObstacleListObjectPool.CountAll;
        __CollisionAsteroidPoolSize = CollisionAsteroidPool.CountAll;
        __MiningAsteroidPoolSize = MiningAsteroidPool.CountAll;
        __SquadPoolSize = SquadPool.CountAll;
        __CarrierSquadPoolSize = CarrierSquadPool.CountAll;
        __AggressiveCommandPoolSize = AggressiveCommandPool.CountAll;
        __BombingRunCommandPoolSize = BombingRunCommandPool.CountAll;
        __ChargeCommandPoolSize = ChargeCommandPool.CountAll;
        __CircleSquadCommandPoolSize = CircleSquadCommandPool.CountAll;
        __ClosestFriendlyCommandPoolSize = ClosestFriendlyCommandPool.CountAll;
        __FullRetreatCommandPoolSize = FullRetreatCommandPool.CountAll;
        __GuardCommandPoolSize = GuardCommandPool.CountAll;
        __InAndOutCommandPoolSize = InAndOutCommandPool.CountAll;
        __MiningCommandPoolSize = MiningCommandPool.CountAll;
        __MoveToRandomCommandPoolSize = MoveToRandomCommandPool.CountAll;
        __PatrolCommandPoolSize = PatrolCommandPool.CountAll;
        __RetreatCommandPoolSize = RetreatCommandPool.CountAll;
        __ScoutingCommandPoolSize = ScoutingCommandPool.CountAll;
        __SwipeSquadCommandPoolSize = SwipeSquadCommandPool.CountAll;
    }

    public void Setup(Stage stage)
    {
        Stage = stage;

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
        MiningAsteroidPool = new ObjectPool<MiningAsteroid>(CreatePooledMiningAsteroid, null, null, null, true);

        SquadPool = new ObjectPool<Squad>(CreatePooledSquad, null, null, null, true);
        CarrierSquadPool = new ObjectPool<CarrierSquad>(CreatePooledCarrierSquad, null, null, null, true);

        AggressiveCommandPool = new ObjectPool<Aggressive>(CreatePooledAggressiveCommand, null, null, null, true);
        BombingRunCommandPool = new ObjectPool<BombingRun>(CreatePooledBombingRunCommand, null, null, null, true);
        ChargeCommandPool = new ObjectPool<Charge>(CreatePooledChargeCommand, null, null, null, true);
        CircleSquadCommandPool = new ObjectPool<CircleSquad>(CreatePooledCircleSquadCommand, null, null, null, true);
        ClosestFriendlyCommandPool = new ObjectPool<ClosestFriendly>(CreatePooledClosestFriendlyCommand, null, null, null, true);
        FullRetreatCommandPool = new ObjectPool<FullRetreat>(CreatePooledFullRetreatCommand, null, null, null, true);
        GuardCommandPool = new ObjectPool<Guard>(CreatePooledGuardCommand, null, null, null, true);
        InAndOutCommandPool = new ObjectPool<InAndOut>(CreatePooledInAndOutCommand, null, null, null, true);
        MiningCommandPool = new ObjectPool<Mining>(CreatePooledMiningCommand, null, null, null, true);
        MoveToRandomCommandPool = new ObjectPool<MoveToRandom>(CreatePooledMoveToRandomCommand, null, null, null, true);
        PatrolCommandPool = new ObjectPool<Patrol>(CreatePooledPatrolCommand, null, null, null, true);
        RetreatCommandPool = new ObjectPool<Retreat>(CreatePooledRetreatCommand, null, null, null, true);
        ScoutingCommandPool = new ObjectPool<Scouting>(CreatePooledScoutingCommand, null, null, null, true);
        SwipeSquadCommandPool = new ObjectPool<SwipeSquad>(CreatePooledSwipeSquadCommand, null, null, null, true);

        //FillPools();
    }
    public Aggressive CreatePooledAggressiveCommand()
    {
        Aggressive command = gameObject.AddComponent<Aggressive>();
        command.Create(Stage);
        return command;
    }
    public BombingRun CreatePooledBombingRunCommand()
    {
        BombingRun command = gameObject.AddComponent<BombingRun>();
        command.Create(Stage);
        return command;
    }
    public Charge CreatePooledChargeCommand()
    {
        Charge command = gameObject.AddComponent<Charge>();
        command.Create(Stage);
        return command;
    }
    public CircleSquad CreatePooledCircleSquadCommand()
    {
        CircleSquad command = gameObject.AddComponent<CircleSquad>();
        command.Create(Stage);
        return command;
    }
    public ClosestFriendly CreatePooledClosestFriendlyCommand()
    {
        ClosestFriendly command = gameObject.AddComponent<ClosestFriendly>();
        command.Create(Stage);
        return command;
    }
    public FullRetreat CreatePooledFullRetreatCommand()
    {
        FullRetreat command = gameObject.AddComponent<FullRetreat>();
        command.Create(Stage);
        return command;
    }
    public Guard CreatePooledGuardCommand()
    {
        Guard command = gameObject.AddComponent<Guard>();
        command.Create(Stage);
        return command;
    }
    public InAndOut CreatePooledInAndOutCommand()
    {
        InAndOut command = gameObject.AddComponent<InAndOut>();
        command.Create(Stage);
        return command;
    }
    public Mining CreatePooledMiningCommand()
    {
        Mining command = gameObject.AddComponent<Mining>();
        command.Create(Stage);
        return command;
    }
    public MoveToRandom CreatePooledMoveToRandomCommand()
    {
        MoveToRandom command = gameObject.AddComponent<MoveToRandom>();
        command.Create(Stage);
        return command;
    }
    public Patrol CreatePooledPatrolCommand()
    {
        Patrol command = gameObject.AddComponent<Patrol>();
        command.Create(Stage);
        return command;
    }
    public Retreat CreatePooledRetreatCommand()
    {
        Retreat command = gameObject.AddComponent<Retreat>();
        command.Create(Stage);
        return command;
    }
    public Scouting CreatePooledScoutingCommand()
    {
        Scouting command = gameObject.AddComponent<Scouting>();
        command.Create(Stage);
        return command;
    }
    public SwipeSquad CreatePooledSwipeSquadCommand()
    {
        SwipeSquad command = gameObject.AddComponent<SwipeSquad>();
        command.Create(Stage);
        return command;
    }
    public CarrierSquad CreatePooledCarrierSquad()
    {
        CarrierSquad carrierSquad = gameObject.AddComponent<CarrierSquad>();
        carrierSquad.Create(Stage);
        return carrierSquad;
    }
    public Squad CreatePooledSquad()
    {
        Squad squad = gameObject.AddComponent<Squad>();
        squad.Create(Stage);
        return squad;
    }
    public CollisionAsteroid CreatePooledCollisionAsteroid()
    {
        CollisionAsteroid asteroid = Instantiate(Stage.Prefabs.CollisionAsteroidPrefabs[Utilities.RandomInt(Stage.Prefabs.CollisionAsteroidPrefabs.Count)]).GetComponent<CollisionAsteroid>();
        asteroid.Create(Stage);
        return asteroid;
    }
    public MiningAsteroid CreatePooledMiningAsteroid()
    {
        MiningAsteroid asteroid = Instantiate(Stage.Prefabs.MiningAsteroidPrefabs[Utilities.RandomInt(Stage.Prefabs.MiningAsteroidPrefabs.Count)]).GetComponent<MiningAsteroid>();
        asteroid.Create(Stage);
        return asteroid;
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
    public ObstacleMap CreatePooledObstacleList(int index)
    {
        ObstacleMap obstacleMap = new ObstacleMap(ItemCount++);
        switch (index)
        {
            case 0:
                Stage.Prefabs.EmptyObstacleList.ForEach((prefab) =>
                {
                    Obstacle obstacle = Instantiate(prefab).GetComponent<Obstacle>();
                    obstacleMap.Obstacles.Add(obstacle);
                });
                break;

            case 1:
                Stage.Prefabs.MazePrefabs.ForEach((prefab) =>
                {
                    Obstacle obstacle = Instantiate(prefab).GetComponent<Obstacle>();
                    obstacleMap.Obstacles.Add(obstacle);
                });
                break;

            case 2:
                Stage.Prefabs.ThreePathsPrefabs.ForEach((prefab) =>
                {
                    Obstacle obstacle = Instantiate(prefab).GetComponent<Obstacle>();
                    obstacleMap.Obstacles.Add(obstacle);
                });
                break;

            case 3:
                Stage.Prefabs.ForestPrefabs.ForEach((prefab) =>
                {
                    Obstacle obstacle = Instantiate(prefab).GetComponent<Obstacle>();
                    obstacleMap.Obstacles.Add(obstacle);
                });
                break;

            case 4:
                Stage.Prefabs.TheWallPrefabs.ForEach((prefab) =>
                {
                    Obstacle obstacle = Instantiate(prefab).GetComponent<Obstacle>();
                    obstacleMap.Obstacles.Add(obstacle);
                });
                break;
            default:
                Debug.LogError($"The chosen obstacle map index does not match an obstacle map");
                break;
        }
        return obstacleMap;
    }
    public Barge CreatePooledBarge()
    {
        Barge barge = Instantiate(Stage.Prefabs.BargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Barge>();
        barge.Create(Stage);
        return barge;
    }
    public Beacon CreatePooledBeacon()
    {
        Beacon beacon = Instantiate(Stage.Prefabs.BeaconPrefab, Vector2.zero, Quaternion.identity).GetComponent<Beacon>();
        beacon.Create(Stage);
        return beacon;
    }
    public Beehive CreatePooledBeehive()
    {
        Beehive Beehive = Instantiate(Stage.Prefabs.BeehivePrefab, Vector2.zero, Quaternion.identity).GetComponent<Beehive>();
        Beehive.Create(Stage);
        return Beehive;
    }
    public Bumblebee CreatePooledBumblebee()
    {
        Bumblebee Bumblebee = Instantiate(Stage.Prefabs.BumblebeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Bumblebee>();
        Bumblebee.Create(Stage);
        return Bumblebee;
    }
    public CarpenterBee CreatePooledCarpenterBee()
    {
        CarpenterBee CarpenterBee = Instantiate(Stage.Prefabs.CarpenterBeePrefab, Vector2.zero, Quaternion.identity).GetComponent<CarpenterBee>();
        CarpenterBee.Create(Stage);
        return CarpenterBee;
    }
    public Carrier CreatePooledCarrier()
    {
        Carrier Carrier = Instantiate(Stage.Prefabs.CarrierPrefab, Vector2.zero, Quaternion.identity).GetComponent<Carrier>();
        Carrier.Create(Stage);
        return Carrier;
    }
    public Cruiser CreatePooledCruiser()
    {
        Cruiser Cruiser = Instantiate(Stage.Prefabs.CruiserPrefab, Vector2.zero, Quaternion.identity).GetComponent<Cruiser>();
        Cruiser.Create(Stage);
        return Cruiser;
    }
    public Dreadnought CreatePooledDreadnought()
    {
        Dreadnought Dreadnought = Instantiate(Stage.Prefabs.DreadnoughtPrefab, Vector2.zero, Quaternion.identity).GetComponent<Dreadnought>();
        Dreadnought.Create(Stage);
        return Dreadnought;
    }
    public Drone CreatePooledDrone()
    {
        Drone Drone = Instantiate(Stage.Prefabs.DronePrefab, Vector2.zero, Quaternion.identity).GetComponent<Drone>();
        Drone.Create(Stage);
        return Drone;
    }
    public Factory CreatePooledFactory()
    {
        Factory Factory = Instantiate(Stage.Prefabs.FactoryPrefab, Vector2.zero, Quaternion.identity).GetComponent<Factory>();
        Factory.Create(Stage);
        return Factory;
    }
    public FireBarge CreatePooledFireBarge()
    {
        FireBarge FireBarge = Instantiate(Stage.Prefabs.FireBargePrefab, Vector2.zero, Quaternion.identity).GetComponent<FireBarge>();
        FireBarge.Create(Stage);
        return FireBarge;
    }
    public Flagship CreatePooledFlagship()
    {
        Flagship Flagship = Instantiate(Stage.Prefabs.FlagshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Flagship>();
        Flagship.Create(Stage);
        return Flagship;
    }
    public Frigate CreatePooledFrigate()
    {
        Frigate Frigate = Instantiate(Stage.Prefabs.FrigatePrefab, Vector2.zero, Quaternion.identity).GetComponent<Frigate>();
        Frigate.Create(Stage);
        return Frigate;
    }
    public Gunship CreatePooledGunship()
    {
        Gunship Gunship = Instantiate(Stage.Prefabs.GunshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Gunship>();
        Gunship.Create(Stage);
        return Gunship;
    }
    public Honeybee CreatePooledHoneybee()
    {
        Honeybee Honeybee = Instantiate(Stage.Prefabs.HoneybeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Honeybee>();
        Honeybee.Create(Stage);
        return Honeybee;
    }
    public Hornet CreatePooledHornet()
    {
        Hornet Hornet = Instantiate(Stage.Prefabs.HornetPrefab, Vector2.zero, Quaternion.identity).GetComponent<Hornet>();
        Hornet.Create(Stage);
        return Hornet;
    }
    public Leafcutter CreatePooledLeafcutter()
    {
        Leafcutter Leafcutter = Instantiate(Stage.Prefabs.LeafcutterPrefab, Vector2.zero, Quaternion.identity).GetComponent<Leafcutter>();
        Leafcutter.Create(Stage);
        return Leafcutter;
    }
    public Queen CreatePooledQueen()
    {
        Queen Queen = Instantiate(Stage.Prefabs.QueenPrefab, Vector2.zero, Quaternion.identity).GetComponent<Queen>();
        Queen.Create(Stage);
        return Queen;
    }
    public Scout CreatePooledScout()
    {
        Scout Scout = Instantiate(Stage.Prefabs.ScoutPrefab, Vector2.zero, Quaternion.identity).GetComponent<Scout>();
        Scout.Create(Stage);
        return Scout;
    }
    public Striker CreatePooledStriker()
    {
        Striker Striker = Instantiate(Stage.Prefabs.StrikerPrefab, Vector2.zero, Quaternion.identity).GetComponent<Striker>();
        Striker.Create(Stage);
        return Striker;
    }
    public WarpGate CreatePooledWarpGate()
    {
        WarpGate WarpGate = Instantiate(Stage.Prefabs.WarpGatePrefab, Vector2.zero, Quaternion.identity).GetComponent<WarpGate>();
        WarpGate.Create(Stage);
        return WarpGate;
    }
    public Wasp CreatePooledWasp()
    {
        Wasp Wasp = Instantiate(Stage.Prefabs.WaspPrefab, Vector2.zero, Quaternion.identity).GetComponent<Wasp>();
        Wasp.Create(Stage);
        return Wasp;
    }
    public YellowJacket CreatePooledYellowJacket()
    {
        YellowJacket YelllowJacket = Instantiate(Stage.Prefabs.YellowJacketPrefab, Vector2.zero, Quaternion.identity).GetComponent<YellowJacket>();
        YelllowJacket.Create(Stage);
        return YelllowJacket;
    }

    public Assets.Scripts.UI_Components.Map CreatePooledPlutoMap()
    {
        return CreatePooledMap(0);
    }
    public Assets.Scripts.UI_Components.Map CreatePooledUranusMap()
    {
        return CreatePooledMap(1);
    }
    public Assets.Scripts.UI_Components.Map CreatePooledMap(int index)
    {
        Assets.Scripts.UI_Components.Map map = Instantiate(Stage.Prefabs.Maps[index]).GetComponent<Assets.Scripts.UI_Components.Map>();
        map.Setup(index, ItemCount++, ConfigData.Maps[index].Name, ConfigData.Maps[index].UserStartingPosition, ConfigData.Maps[index].AIStartingPosition);
        map.name = map.Name;
        return map;
    }

    public Projectile CreatePooledBeeSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeeSmallLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledBeeMediumProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeeMediumLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledBumblebeeShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BumblebeeShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledFlagshipShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.FlagshipShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledRocketProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.RocketPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledHumanSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.HumanSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledHumanMediumProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.HumanMediumPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledBeamProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeamPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledSplitShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.SplitShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledQueenSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.QueenSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledQueenLargeProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.QueenLargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledStrikerBombProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.StrikerBombPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledRocketExplosionProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.RocketExplosionPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatePooledFireBargeExplosionProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.FireBargeExplosionPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }

    public void OnTakeShipFromPool(Ship ship)
    {
        Debug.Log($"{ship.name} was taken from the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void OnReturnShipToPool(Ship ship)
    {
        Debug.Log($"{ship.name} was returned to the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void OnTakeProjectileFromPool(Projectile projectile)
    {
        Debug.Log($"{projectile.Name} was taken from the pool");
    }

    public void OnReturnProjectileToPool(Projectile projectile)
    {
        Debug.Log($"{projectile.Name} was returned to the pool");
    }
    public CarrierSquad GetCarrierSquadFromPool()
    {
        return CarrierSquadPool.Get();
    }
    public void ReturnSquadToPool(Squad squad)
    {
        if (squad.SquadType != ConfigData.SquadTypes.CarrierSquad)
        {
            SquadPool.Release(squad);
        }
        else
        {
            CarrierSquadPool.Release((CarrierSquad)squad);
        }
    }
    public Squad GetSquadFromPool()
    {
        return SquadPool.Get();
    }
    public void ReturnMiningAsteroidToPool(MiningAsteroid asteroid)
    {
        asteroid.gameObject.SetActive(false);
        MiningAsteroidPool.Release(asteroid);
    }
    public MiningAsteroid GetMiningAsteroidFromPool()
    {
        return MiningAsteroidPool.Get();
    }

    public void ReturnCollisionAsteroidToPool(CollisionAsteroid asteroid)
    {
        asteroid.gameObject.SetActive(false);
        CollisionAsteroidPool.Release(asteroid);
    }
    public CollisionAsteroid GetCollisionAsteroidFromPool()
    {
        return CollisionAsteroidPool.Get();
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
        switch (index)
        {
            case 0:
                return EmptyObstacleListObjectPool.Get();

            case 1:
                return MazeObstacleListObjectPool.Get();

            case 2:
                return ThreePathsObstacleListObjectPool.Get();

            case 3:
                return ForestObstacleListObjectPool.Get();

            case 4:
                return TheWallObstacleListObjectPool.Get();
            default:
                Debug.LogError($"The chosen obstacle map index does not match an obstacle map");
                break;
        }
        return null;
    }

    public void ReturnShipToPool(Ship ship)
    {

        ship.gameObject.SetActive(false);
        if (ship.HasMovementMarker)
        {
            ship.MovementMarker.SetActive(false);
        }
        ship.Turrets.ForEach(turret =>
        {
            turret.CancelInvoke();
            if (turret.HasTargetingMarker)
            {
                turret.TargetingMarker.SetActive(false);
            }
        });

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
        Debug.Log($"Getting map from pool");

        switch (index)
        {
            case 0:
                return PlutoMapPool.Get();
            case 1:
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
        Debug.Log($"Returning {map.Name} to pool");
        map.gameObject.SetActive(false);
        switch (map.Index)
        {
            case 0:
                PlutoMapPool.Release(map);
                break;

            case 1:
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
        projectile.gameObject.SetActive(false);
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
        switch(command.CommandType)
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
            default:
                Debug.LogError($"Command type is invalid: {command}");
                break;
        }
    }
    public Command GetCommandFromPool(Command command)
    {
        switch (command.CommandType)
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
            default:
                Debug.LogError($"Command type is invalid: {command}");
                return null;
        }
    }
    private void FillPools()
    {
        int fillSizeSmall = 15 * Stage.LevelCount / 2;
        int fillSizeMedium = 10 * Stage.LevelCount / 2;
        int fillSizeLarge = 5 * Stage.LevelCount / 2;
        List<Ship> spawnedShips = new List<Ship>();
        List<Projectile> spawnedProjectiles = new List<Projectile>();
        List<Command> spawnedCommands = new List<Command>();
        

        for (int i = 0; i < fillSizeSmall; i++)
        {
            spawnedShips.Add(BeaconPool.Get());
            spawnedShips.Add(DronePool.Get());
            spawnedShips.Add(HoneybeePool.Get());
            spawnedShips.Add(HornetPool.Get());
            spawnedShips.Add(ScoutPool.Get());
            spawnedShips.Add(StrikerPool.Get());
            spawnedShips.Add(YellowJacketPool.Get());

            spawnedProjectiles.Add(BeeSmallProjectilePool.Get());
            spawnedProjectiles.Add(HumanSmallProjectilePool.Get());
            spawnedProjectiles.Add(QueenSmallProjectilePool.Get());
        }

        for (int i = 0; i < fillSizeMedium; i++)
        {
            spawnedShips.Add(BargePool.Get());
            spawnedShips.Add(BumblebeePool.Get());
            spawnedShips.Add(CarpenterBeePool.Get());
            spawnedShips.Add(CarrierPool.Get());
            spawnedShips.Add(CruiserPool.Get());
            spawnedShips.Add(DreadnoughtPool.Get());
            spawnedShips.Add(FrigatePool.Get());
            spawnedShips.Add(GunshipPool.Get());
            spawnedShips.Add(LeafcutterPool.Get());
            spawnedShips.Add(WaspPool.Get());

            spawnedProjectiles.Add(BeeMediumProjectilePool.Get());
            spawnedProjectiles.Add(RocketProjectilePool.Get());
            spawnedProjectiles.Add(HumanMediumProjectilePool.Get());
            spawnedProjectiles.Add(SplitShotProjectilePool.Get());
            spawnedProjectiles.Add(StrikerBombProjectilePool.Get());
            //spawnedProjectiles.Add(RocketExplosionProjectilePool.Get());

            spawnedCommands.Add(AggressiveCommandPool.Get());
            spawnedCommands.Add(BombingRunCommandPool.Get());
            spawnedCommands.Add(ChargeCommandPool.Get());
            spawnedCommands.Add(CircleSquadCommandPool.Get());
            spawnedCommands.Add(ClosestFriendlyCommandPool.Get());
            spawnedCommands.Add(FullRetreatCommandPool.Get());
            spawnedCommands.Add(GuardCommandPool.Get());
            spawnedCommands.Add(InAndOutCommandPool.Get());
            spawnedCommands.Add(MiningCommandPool.Get());
            spawnedCommands.Add(MoveToRandomCommandPool.Get());
            spawnedCommands.Add(PatrolCommandPool.Get());
            spawnedCommands.Add(RetreatCommandPool.Get());
            spawnedCommands.Add(ScoutingCommandPool.Get());
            spawnedCommands.Add(SwipeSquadCommandPool.Get());


        }

        for (int i = 0; i < fillSizeLarge; i++)
        {
            spawnedShips.Add(BeehivePool.Get());
            spawnedShips.Add(FactoryPool.Get());
            spawnedShips.Add(FireBargePool.Get());
            spawnedShips.Add(FlagshipPool.Get());
            spawnedShips.Add(QueenPool.Get());
            spawnedShips.Add(WarpGatePool.Get());

            spawnedProjectiles.Add(BumblebeeShotProjectilePool.Get());
            spawnedProjectiles.Add(FlagshipShotProjectilePool.Get());
            spawnedProjectiles.Add(BeamProjectilePool.Get());
            spawnedProjectiles.Add(QueenLargeProjectilePool.Get());
            spawnedProjectiles.Add(FireBargeExplosionProjectilePool.Get());
        }


        spawnedShips.ForEach((ship) =>
        {
            ReturnShipToPool(ship);
        });

        spawnedProjectiles.ForEach((projectile) =>
        {
            ReturnProjectileToPool(projectile);
        });

        spawnedCommands.ForEach((command) =>
        {

        });

    }

    public void FillAsteroidPool()
    {
        List<CollisionAsteroid> spawnedAsteroids = new List<CollisionAsteroid>();
        for (int i = 0; i < 15 * Stage.LevelCount; i++)
        {
            spawnedAsteroids.Add(CollisionAsteroidPool.Get());
        }
        spawnedAsteroids.ForEach((spawnedAsteroid) =>
        {
            ReturnCollisionAsteroidToPool(spawnedAsteroid);
        });
    }
}
