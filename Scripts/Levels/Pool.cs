using Assets.Scripts;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

public class Pool : MonoBehaviour
{
    public Stage Stage;

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

    public int __BargePoolSize, __BeaconPoolSize, __BeehivePoolSize, __BumblebeePoolSize, __CarpenterBeePoolSize, __CarrierPoolSize, __CruiserPoolSize, __DreadnoughtPoolSize,
        __DronePoolSize, __FactoryPoolSize, __FireBargePoolSize, __FlagshipPoolSize, __FrigatePoolSize, __GunshipPoolSize, __HoneybeePoolSize, __HornetPoolSize, __LeafcutterPoolSize,
        __QueenPoolSize, __ScoutPoolSize, __StrikerPoolSize, __WarpGatePoolSize, __WaspPoolSize, __YellowJacketPoolSize, __PlutoMapPoolSize, __UranusMapPoolSize, __BeeSmallProjectilePoolSize,
        __BeeMediumProjectilePoolSize, __BumblebeeShotProjectilePoolSize, __FlagshipShotProjectilePoolSize, __RocketProjectilePoolSize, __HumanSmallProjectilePoolSize, __HumanMediumProjectilePoolSize,
        __BeamProjectilePoolSize, __SplitShotProjectilePoolSize, __QueenSmallProjectilePoolSize, __QueenLargeProjectilePoolSize, __StrikerBombProjectilePoolSize, __RocketExplosionProjectilePoolSize,
        __FireBargeExplosionProjectilePoolSize;
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

        BeeSmallProjectilePool = new ObjectPool<Projectile>(CreatedPooledBeeSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BeeMediumProjectilePool = new ObjectPool<Projectile>(CreatedPooledBeeMediumProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BumblebeeShotProjectilePool = new ObjectPool<Projectile>(CreatedPooledBumblebeeShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        FlagshipShotProjectilePool = new ObjectPool<Projectile>(CreatedPooledFlagshipShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        RocketProjectilePool = new ObjectPool<Projectile>(CreatedPooledRocketProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        HumanSmallProjectilePool = new ObjectPool<Projectile>(CreatedPooledHumanSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        HumanMediumProjectilePool = new ObjectPool<Projectile>(CreatedPooledHumanMediumProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        BeamProjectilePool = new ObjectPool<Projectile>(CreatedPooledBeamProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        SplitShotProjectilePool = new ObjectPool<Projectile>(CreatedPooledSplitShotProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        QueenSmallProjectilePool = new ObjectPool<Projectile>(CreatedPooledQueenSmallProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        QueenLargeProjectilePool = new ObjectPool<Projectile>(CreatedPooledQueenLargeProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        StrikerBombProjectilePool = new ObjectPool<Projectile>(CreatedPooledStrikerBombProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        RocketExplosionProjectilePool = new ObjectPool<Projectile>(CreatedPooledRocketExplosionProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);
        FireBargeExplosionProjectilePool = new ObjectPool<Projectile>(CreatedPooledFireBargeExplosionProjectile, OnTakeProjectileFromPool, OnReturnProjectileToPool, null, true);

        FillPools();
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
        map.Setup(index, ConfigData.Maps[index].Name, ConfigData.Maps[index].UserStartingPosition, ConfigData.Maps[index].AIStartingPosition);
        map.name = map.Name;
        return map;
    }

    public Projectile CreatedPooledBeeSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeeSmallLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledBeeMediumProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeeMediumLaserShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledBumblebeeShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BumblebeeShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledFlagshipShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.FlagshipShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledRocketProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.RocketPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledHumanSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.HumanSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledHumanMediumProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.HumanMediumPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledBeamProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.BeamPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledSplitShotProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.SplitShotPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledQueenSmallProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.QueenSmallPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledQueenLargeProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.QueenLargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledStrikerBombProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.StrikerBombPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledRocketExplosionProjectile()
    {
        Projectile projectile = Instantiate(Stage.Prefabs.RocketExplosionPrefab, Vector2.zero, Quaternion.identity).GetComponent<Projectile>();
        projectile.Create(Stage);
        return projectile;
    }
    public Projectile CreatedPooledFireBargeExplosionProjectile()
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
        Debug.Log($"{projectile.name} was taken from the pool");
    }

    public void OnReturnProjectileToPool(Projectile projectile)
    {
        Debug.Log($"{projectile.name} was returned to the pool");
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
    private void FillPools()
    {
        int fillSizeSmall = 15 * Stage.LevelCount / 2;
        int fillSizeMedium = 10 * Stage.LevelCount / 2;
        int fillSizeLarge = 5 * Stage.LevelCount / 2;
        List<Ship> spawnedShips = new List<Ship>();
        List<Projectile> spawnedProjectiles = new List<Projectile>();

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
            spawnedProjectiles.Add(RocketExplosionProjectilePool.Get());


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

    }
}
