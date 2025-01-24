using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using Assets.Scripts.UIComponents;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Container scene for 1 or more Levels. Handles scene level variables and communication with the server
/// </summary>
public class Stage : Scene
{
    /// <summary>
    /// The number of levels to be spawned on this stage
    /// </summary>
    public int LevelCount;
    /// <summary>
    /// Whether or not the user is playing the game and controlling a side
    /// </summary>
    public bool DoesUserHaveController;
    /// <summary>
    /// Whether or not the game is being debugged and should log a lot of debugging data
    /// </summary>
    public bool IsDebugging;
    /// <summary>
    /// Whether or not the Neural Network is being trained
    /// </summary>
    public bool IsTrainingNueralNetwork;
    /// <summary>
    /// Whether or not the Hive Mind is being trained
    /// </summary>
    public bool IsTrainingHiveMind;
    /// <summary>
    /// Whether or not any AI training is taking place
    /// </summary>
    public bool IsTraining;
    /// <summary>
    /// Whether or not the AI is controlled by the Nueral Network
    /// </summary>
    public bool ActivateBrains;
    /// <summary>
    /// Whether or not the Hive Mind is active and giving commands
    /// </summary>
    public bool ActivateHiveMind;
    /// <summary>
    /// Whether or not their is audio playing for the Primary Level
    /// </summary>
    public bool ActivateAudio;
    /// <summary>
    /// Whether or not music is playing for the Primary Level. Audio must be activated
    /// </summary>
    public bool PlayMusic;
    /// <summary>
    /// Whether or not to have the camera locked to the Primary Level or zoomed out above the levels
    /// </summary>
    public bool UnlockCamera;
    /// <summary>
    /// Determines whether or not FleetShips get marked as dead when ships die. If this is turned off, stats will still record properly but ships won't die off and be replaced
    /// </summary>
    public bool ReplaceDeadShips;
    /// <summary>
    /// Whether or not stats will be recorded
    /// </summary>
    public bool RecordStats;
    /// <summary>
    /// Whether or not all squads will be randomly generated
    /// </summary>
    public bool UseFullyRandomSquads;
    /// <summary>
    /// Whether or not enemy squads will be randomly generated
    /// </summary>
    public bool UseFullyRandomEnemySquads;
    /// <summary>
    /// Whether or not override squads (Specific squads created in the squad maker) will be used
    /// </summary>
    public bool UseOverrideSquads;
    /// <summary>
    /// Whether or not enemy override squads (Specific squads created in the squad maker) will be used
    /// </summary>
    public bool UseOverrideEnemySquads;
    /// <summary>
    /// Turns on/off camera scrolling when the mouse is at the edge of the screen
    /// </summary>
    public bool UseMouseScrolling;
    /// <summary>
    /// Makes the player's selected ships fire towards the mouse
    /// </summary>
    public bool IsTestFiring;
    /// <summary>
    /// Makes the enemy not shoot
    /// </summary>
    public bool MakeEnemyCeaseFire;
    /// <summary>
    /// Prevents all ships from shooting except for manual fire
    /// </summary>
    public bool FullCeaseFire;
    /// <summary>
    /// Makes all projectiles inflict zero damage
    /// </summary>
    public bool MakeShotsHarmless;
    /// <summary>
    /// Whether or not to allow randomized options for the levels
    /// </summary>
    public bool HasRandomizedOptions;
    /// <summary>
    /// Overrides the user side with either 1 (Bees) or 2 (Humans)
    /// </summary>
    public int OverrideUserSide;
    /// <summary>
    /// The amount of time in seconds that must elapse before the level resets if the levels are training
    /// </summary>
    public int TimeoutTime;
    /// <summary>
    /// What time scale to set the game to. 0 = Default, 1+ = override the default
    /// </summary>
    public int OverrideTimeScale;
    /// <summary>
    /// The upper limit on how many squads to generate
    /// </summary>
    public int GeneratedSquadCountOverride;
    /// <summary>
    /// Forces all levels to use a particular map
    /// </summary>
    public int OverrideMapIndex;
    /// <summary>
    /// Forces all levels to use a particular set of obstacles
    /// </summary>
    public int OverrideObstacleMapIndex;
    /// <summary>
    /// Multiplies the speed of ships
    /// </summary>
    public int SpeedMultiplier;
    /// <summary>
    /// Initial delay before hive mind commands are requested
    /// </summary>
    public int InitialCommandDelay;
    /// <summary>
    /// How frequently asteroids spawn in this level. Sets the upper and lower bounds in seconds of the randomly timed spawn
    /// </summary>
    public int AsteroidMaxSpawnRate, AsteroidMinimumSpawnRate, CurrentAsteroidMaxSpawnRate, CurrentAsteroidMinimumSpawnRate;
    /// <summary>
    /// Sets the upper bounds for how fast an asteroid can move
    /// </summary>
    public int AsteroidMaxSpeed;
    /// <summary>
    /// The default zoom level for the camera
    /// </summary>
    public int DefaultZoom;
    /// <summary>
    /// How fast the camera zooms in and out 
    /// </summary>
    public int ZoomSpeed;
    /// <summary>
    /// How fast the camera scrolls side to side or up and down
    /// </summary>
    public int ScrollSpeed;
    /// <summary>
    /// How close the mouse needs to be to the edge for the camera to start scrolling
    /// </summary>
    public Vector2 MouseScrollDistanceFromEdge;
    /// <summary>
    /// The default position for the camera before it's repositioned
    /// </summary>
    public Vector2 DefaultCameraPosition;
    /// <summary>
    /// Only allows Bee ship types as specified here, unless it's empty
    /// </summary>
    public List<string> OverrideBeeShipTypes = new List<string> { };
    /// <summary>
    /// Only allows Human ship types as specified here, unless it's empty
    /// </summary>
    public List<string> OverrideHumanShipTypes = new List<string> { };
    /// <summary>
    /// Only allows Hive Mind strats of the types specified here, unless it's empty
    /// </summary>
    public List<string> OverrideStrats = new List<string> { };
    /// <summary>
    /// The set of positions for each level depending on the number of levels on the stage
    /// </summary>
    public Dictionary<int, Vector2[]> LevelLayouts = new Dictionary<int, Vector2[]>
    {
        {1, new Vector2[] { new Vector2(0, 0), new Vector2(0, 0) } },
        {2, new Vector2[] { new Vector2(-756, 0), new Vector2(756, 0) } },
        {4, new Vector2[] { new Vector2(-756, 756), new Vector2(756, 756), new Vector2(-756, -756), new Vector2(756, -756) } },
    };
    /// <summary>
    /// All the clearances for all the ships, calculated in levels dynamically when needed but shared between all the levels
    /// </summary>
    public Dictionary<string, int> ShipClearances = new Dictionary<string, int>();
    /// <summary>
    /// The sprite used for user ship vision to clear the fog of war
    /// </summary>
    public Sprite VisonSprite;
    /// <summary>
    /// Holds all the entity prefabs for the game (Ships, projectiles, Obstacles, Asteroids, etc.)
    /// </summary>
    public Prefabs Prefabs;
    /// <summary>
    /// The UI Menus
    /// </summary>
    public GameMenus Menus;
    /// <summary>
    /// Controls the selection box
    /// </summary>
    public Selector Selector;
    /// <summary>
    /// Handles all input for the Primary Level
    /// </summary>
    public LevelInputManager InputManager;
    /// <summary>
    /// Manages audio for the Primary Level
    /// </summary>
    public AudioController Audio;
    /// <summary>
    /// The camera that outputs to the mini map
    /// </summary>
    public Camera MiniMapCamera;
    /// <summary>
    /// Takes care of miscellaneous UI interaction
    /// </summary>
    public GameObject UIManager;
    /// <summary>
    /// The box for selecting squads and patrol areas
    /// </summary>
    public GameObject SelectionBox;
    /// <summary>
    /// The container for the MiniMap Camera
    /// </summary>
    public GameObject MiniMapCameraContainer; 
    /// <summary>
    /// The canvas that the mini map camera projects to
    /// </summary>
    public GameObject MiniMapDisplayCanvas;
    /// <summary>
    /// The list of squad tabs across the top of the UI
    /// </summary>
    public List<SquadTab> SquadTabs;
    /// <summary>
    /// The main level that accepts user interaction
    /// </summary>
    public Level PrimaryLevel;
    /// <summary>
    /// All the levels that this stage has spawned
    /// </summary>
    public List<Level> Levels;
    /// <summary>
    /// The current Bee ship types available for the levels
    /// </summary>
    public List<string> BeeShipTypes = new List<string>();
    /// <summary>
    /// The current Human ship types available for the levels
    /// </summary>
    public List<string> HumanShipTypes = new List<string>();
    /// <summary>
    /// How many fixed updates have passed since the stage spawned
    /// </summary>
    public int FixedUpdates;
    /// <summary>
    /// The time in seconds when the stage started up
    /// </summary>
    public float StartTime;

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

    //public GameObject PoolShips;

    public int __BargePoolSize, __BeaconPoolSize, __BeehivePoolSize, __BumblebeePoolSize, __CarpenterBeePoolSize, __CarrierPoolSize, __CruiserPoolSize, __DreadnoughtPoolSize,
        __DronePoolSize, __FactoryPoolSize, __FireBargePoolSize, __FlagshipPoolSize, __FrigatePoolSize, __GunshipPoolSize, __HoneybeePoolSize, __HornetPoolSize, __LeafcutterPoolSize,
        __QueenPoolSize, __ScoutPoolSize, __StrikerPoolSize, __WarpGatePoolSize, __WaspPoolSize, __YellowJacketPoolSize;
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
    }



    public Barge CreatePooledBarge()
    {
        Barge barge = Instantiate(Prefabs.BargePrefab, Vector2.zero, Quaternion.identity).GetComponent<Barge>();
        barge.Create(this, "Barge");
        return barge;
    }
    public Beacon CreatePooledBeacon()
    {
        Beacon beacon = Instantiate(Prefabs.BeaconPrefab, Vector2.zero, Quaternion.identity).GetComponent<Beacon>();
        beacon.Create(this, "Beacon");
        return beacon;
    }
    public Beehive CreatePooledBeehive()
    {
        Beehive Beehive = Instantiate(Prefabs.BeehivePrefab, Vector2.zero, Quaternion.identity).GetComponent<Beehive>();
        Beehive.Create(this, "Beehive");
        return Beehive;
    }
    public Bumblebee CreatePooledBumblebee()
    {
        Bumblebee Bumblebee = Instantiate(Prefabs.BumblebeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Bumblebee>();
        Bumblebee.Create(this, "Bumblebee");
        return Bumblebee;
    }
    public CarpenterBee CreatePooledCarpenterBee()
    {
        CarpenterBee CarpenterBee = Instantiate(Prefabs.CarpenterBeePrefab, Vector2.zero, Quaternion.identity).GetComponent<CarpenterBee>();
        CarpenterBee.Create(this, "Carpenter Bee");
        return CarpenterBee;
    }
    public Carrier CreatePooledCarrier()
    {
        Carrier Carrier = Instantiate(Prefabs.CarrierPrefab, Vector2.zero, Quaternion.identity).GetComponent<Carrier>();
        Carrier.Create(this, "Carrier");
        return Carrier;
    }
    public Cruiser CreatePooledCruiser()
    {
        Cruiser Cruiser = Instantiate(Prefabs.CruiserPrefab, Vector2.zero, Quaternion.identity).GetComponent<Cruiser>();
        Cruiser.Create(this, "Cruiser");
        return Cruiser;
    }
    public Dreadnought CreatePooledDreadnought()
    {
        Dreadnought Dreadnought = Instantiate(Prefabs.DreadnoughtPrefab, Vector2.zero, Quaternion.identity).GetComponent<Dreadnought>();
        Dreadnought.Create(this, "Dreadnought");
        return Dreadnought;
    }
    public Drone CreatePooledDrone()
    {
        Drone Drone = Instantiate(Prefabs.DronePrefab, Vector2.zero, Quaternion.identity).GetComponent<Drone>();
        Drone.Create(this, "Drone");
        return Drone;
    }
    public Factory CreatePooledFactory()
    {
        Factory Factory = Instantiate(Prefabs.FactoryPrefab, Vector2.zero, Quaternion.identity).GetComponent<Factory>();
        Factory.Create(this, "Factory");
        return Factory;
    }
    public FireBarge CreatePooledFireBarge()
    {
        FireBarge FireBarge = Instantiate(Prefabs.FireBargePrefab, Vector2.zero, Quaternion.identity).GetComponent<FireBarge>();
        FireBarge.Create(this, "Fire Barge");
        return FireBarge;
    }
    public Flagship CreatePooledFlagship()
    {
        Flagship Flagship = Instantiate(Prefabs.FlagshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Flagship>();
        Flagship.Create(this, "Flagship");
        return Flagship;
    }
    public Frigate CreatePooledFrigate()
    {
        Frigate Frigate = Instantiate(Prefabs.FrigatePrefab, Vector2.zero, Quaternion.identity).GetComponent<Frigate>();
        Frigate.Create(this, "Frigate");
        return Frigate;
    }
    public Gunship CreatePooledGunship()
    {
        Gunship Gunship = Instantiate(Prefabs.GunshipPrefab, Vector2.zero, Quaternion.identity).GetComponent<Gunship>();
        Gunship.Create(this, "Gunship");
        return Gunship;
    }
    public Honeybee CreatePooledHoneybee()
    {
        Honeybee Honeybee = Instantiate(Prefabs.HoneybeePrefab, Vector2.zero, Quaternion.identity).GetComponent<Honeybee>();
        Honeybee.Create(this, "Honeybee");
        return Honeybee;
    }
    public Hornet CreatePooledHornet()
    {
        Hornet Hornet = Instantiate(Prefabs.HornetPrefab, Vector2.zero, Quaternion.identity).GetComponent<Hornet>();
        Hornet.Create(this, "Hornet");
        return Hornet;
    }
    public Leafcutter CreatePooledLeafcutter()
    {
        Leafcutter Leafcutter = Instantiate(Prefabs.LeafcutterPrefab, Vector2.zero, Quaternion.identity).GetComponent<Leafcutter>();
        Leafcutter.Create(this, "Leafcutter");
        return Leafcutter;
    }
    public Queen CreatePooledQueen()
    {
        Queen Queen = Instantiate(Prefabs.QueenPrefab, Vector2.zero, Quaternion.identity).GetComponent<Queen>();
        Queen.Create(this, "Queen");
        return Queen;
    }
    public Scout CreatePooledScout()
    {
        Scout Scout = Instantiate(Prefabs.ScoutPrefab, Vector2.zero, Quaternion.identity).GetComponent<Scout>();
        Scout.Create(this, "Scout");
        return Scout;
    }
    public Striker CreatePooledStriker()
    {
        Striker Striker = Instantiate(Prefabs.StrikerPrefab, Vector2.zero, Quaternion.identity).GetComponent<Striker>();
        Striker.Create(this, "Striker");
        return Striker;
    }
    public WarpGate CreatePooledWarpGate()
    {
        WarpGate WarpGate = Instantiate(Prefabs.WarpGatePrefab, Vector2.zero, Quaternion.identity).GetComponent<WarpGate>();
        WarpGate.Create(this, "Warp Gate");
        return WarpGate;
    }
    public Wasp CreatePooledWasp()
    {
        Wasp Wasp = Instantiate(Prefabs.WaspPrefab, Vector2.zero, Quaternion.identity).GetComponent<Wasp>();
        Wasp.Create(this, "Wasp");
        return Wasp;
    }
    public YellowJacket CreatePooledYellowJacket()
    {
        YellowJacket YelllowJacket = Instantiate(Prefabs.YellowJacketPrefab, Vector2.zero, Quaternion.identity).GetComponent<YellowJacket>();
        YelllowJacket.Create(this, "Yellow Jacket");
        return YelllowJacket;
    }

    public void OnTakeFromPool(Ship ship)
    {
        Debug.Log($"{ship.name} was taken from the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void OnReturnToPool(Ship ship)
    {
        Debug.Log($"{ship.Name} was returned to the pool");
        //ship.transform.parent = PoolShips.transform;
        //ship.transform.localPosition = Vector2.zero;
    }

    public void ReturnShipToPool(Ship ship)
    {

        ship.gameObject.SetActive(false);
        switch (ship.ShipType)
        {
            case "Barge":
                BargePool.Release((Barge)ship);
                break;

            case "Beacon":
                BeaconPool.Release((Beacon)ship);
                break;

            case "Beehive":
                BeehivePool.Release((Beehive)ship);
                break;

            case "Bumblebee":
                BumblebeePool.Release((Bumblebee)ship);
                break;

            case "CarpenterBee":
                CarpenterBeePool.Release((CarpenterBee)ship);
                break;

            case "Carrier":
                CarrierPool.Release((Carrier)ship);
                break;

            case "Cruiser":
                CruiserPool.Release((Cruiser)ship);
                break;

            case "Dreadnought":
                DreadnoughtPool.Release((Dreadnought)ship);
                break;

            case "Drone":
                DronePool.Release((Drone)ship);
                break;

            case "Factory":
                FactoryPool.Release((Factory)ship);
                break;

            case "FireBarge":
                FireBargePool.Release((FireBarge)ship);
                break;

            case "Flagship":
                FlagshipPool.Release((Flagship)ship);
                break;

            case "Frigate":
                FrigatePool.Release((Frigate)ship);
                break;

            case "Gunship":
                GunshipPool.Release((Gunship)ship);
                break;

            case "Honeybee":
                HoneybeePool.Release((Honeybee)ship);
                break;

            case "Hornet":
                HornetPool.Release((Hornet)ship);
                break;

            case "Leafcutter":
                LeafcutterPool.Release((Leafcutter)ship);
                break;

            case "Queen":
                QueenPool.Release((Queen)ship);
                break;

            case "Scout":
                ScoutPool.Release((Scout)ship);
                break;

            case "Striker":
                StrikerPool.Release((Striker)ship);
                break;

            case "WarpGate":
                WarpGatePool.Release((WarpGate)ship);
                break;

            case "Wasp":
                WaspPool.Release((Wasp)ship);
                break;

            case "YellowJacket":
                YellowJacketPool.Release((YellowJacket)ship);
                break;

        }
    }

    // Start is called before the first frame update
    new void Start()
    {
        StartTime = Time.realtimeSinceStartup;
        Debug.Log($"Start level stage");
        Name = "Level";
        base.Start();
    }
    /// <summary>
    /// Spawns the other levels on this stage
    /// </summary>
    private void SpawnLevels()
    {
        Debug.Log($"Spawning stage levels");
        transform.position = LevelLayouts[LevelCount][0];
        for (int i = 0; i < LevelCount; i++)
        {
            GameObject nextLevel = Instantiate(Prefabs.LevelPrefab.gameObject, transform.parent);
            Level level = nextLevel.GetComponent<Level>();
            if (i == 0)
            {
                PrimaryLevel = level;
            }
            nextLevel.transform.parent = transform;
            nextLevel.SetActive(true);
            nextLevel.transform.position = LevelLayouts[LevelCount][i];
            Levels.Add(level);

        }
    }
    private void SetupLevels()
    {
        for (int i = 0; i < Levels.Count; i++)
        {
            Levels[i].Setup(this, $"Level - #{i}");
        }
    }
    private void FillPools()
    {
        int fillSizeSmall = 15 * LevelCount;
        int fillSizeMedium = 10 * LevelCount;
        int fillSizeLarge = 5 * LevelCount;

        for (int i = 0; i < fillSizeSmall; i++)
        {
            BeaconPool.Get();
            DronePool.Get();
            HoneybeePool.Get();
            HornetPool.Get();
            ScoutPool.Get();
            StrikerPool.Get();
            YellowJacketPool.Get();
        }

        for (int i = 0; i < fillSizeMedium; i++)
        {
            BargePool.Get();
            BumblebeePool.Get();
            CarpenterBeePool.Get();
            CarrierPool.Get();
            CruiserPool.Get();
            DreadnoughtPool.Get();
            FrigatePool.Get();
            GunshipPool.Get();
            LeafcutterPool.Get();
            WaspPool.Get();
        }

        for (int i = 0; i < fillSizeLarge; i++)
        {
            BeehivePool.Get();
            FactoryPool.Get();
            FireBargePool.Get();
            FlagshipPool.Get();
            QueenPool.Get();
            WarpGatePool.Get();
        }
    }
    protected override void FinalizeSceneWithUserData()
    {
        Debug.Log($"Finalize scene");


        base.FinalizeSceneWithUserData();

        if (IsMainScene && LevelCount > 0)
        {
            BargePool = new ObjectPool<Barge>(CreatePooledBarge, OnTakeFromPool, OnReturnToPool, null, true);
            BeaconPool = new ObjectPool<Beacon>(CreatePooledBeacon, OnTakeFromPool, OnReturnToPool, null, true);
            BeehivePool = new ObjectPool<Beehive>(CreatePooledBeehive, OnTakeFromPool, OnReturnToPool, null, true);
            BumblebeePool = new ObjectPool<Bumblebee>(CreatePooledBumblebee, OnTakeFromPool, OnReturnToPool, null, true);
            CarpenterBeePool = new ObjectPool<CarpenterBee>(CreatePooledCarpenterBee, OnTakeFromPool, OnReturnToPool, null, true);
            CarrierPool = new ObjectPool<Carrier>(CreatePooledCarrier, OnTakeFromPool, OnReturnToPool, null, true);
            CruiserPool = new ObjectPool<Cruiser>(CreatePooledCruiser, OnTakeFromPool, OnReturnToPool, null, true);
            DreadnoughtPool = new ObjectPool<Dreadnought>(CreatePooledDreadnought, OnTakeFromPool, OnReturnToPool, null, true);
            DronePool = new ObjectPool<Drone>(CreatePooledDrone, OnTakeFromPool, OnReturnToPool, null, true);
            FactoryPool = new ObjectPool<Factory>(CreatePooledFactory, OnTakeFromPool, OnReturnToPool, null, true);
            FireBargePool = new ObjectPool<FireBarge>(CreatePooledFireBarge, OnTakeFromPool, OnReturnToPool, null, true);
            FlagshipPool = new ObjectPool<Flagship>(CreatePooledFlagship, OnTakeFromPool, OnReturnToPool, null, true);
            FrigatePool = new ObjectPool<Frigate>(CreatePooledFrigate, OnTakeFromPool, OnReturnToPool, null, true);
            GunshipPool = new ObjectPool<Gunship>(CreatePooledGunship, OnTakeFromPool, OnReturnToPool, null, true);
            HoneybeePool = new ObjectPool<Honeybee>(CreatePooledHoneybee, OnTakeFromPool, OnReturnToPool, null, true);
            HornetPool = new ObjectPool<Hornet>(CreatePooledHornet, OnTakeFromPool, OnReturnToPool, null, true);
            LeafcutterPool = new ObjectPool<Leafcutter>(CreatePooledLeafcutter, OnTakeFromPool, OnReturnToPool, null, true);
            QueenPool = new ObjectPool<Queen>(CreatePooledQueen, OnTakeFromPool, OnReturnToPool, null, true);
            ScoutPool = new ObjectPool<Scout>(CreatePooledScout, OnTakeFromPool, OnReturnToPool, null, true);
            StrikerPool = new ObjectPool<Striker>(CreatePooledStriker, OnTakeFromPool, OnReturnToPool, null, true);
            WarpGatePool = new ObjectPool<WarpGate>(CreatePooledWarpGate, OnTakeFromPool, OnReturnToPool, null, true);
            WaspPool = new ObjectPool<Wasp>(CreatePooledWasp, OnTakeFromPool, OnReturnToPool, null, true);
            YellowJacketPool = new ObjectPool<YellowJacket>(CreatePooledYellowJacket, OnTakeFromPool, OnReturnToPool, null, true);

            FillPools();
            SpawnLevels();
        }

        if (DoesUserHaveController)
        {
            if ((OverrideUserSide == 1 || OverrideUserSide == 2) && OverrideUserSide != ConfigData.Configuration.UserSide)
            {
                ConfigData.SwapSides();
            }
        }

        if (IsTrainingHiveMind || IsTrainingNueralNetwork)
        {
            IsTraining = true;
        }
        else
        {
            IsTraining = false;
        }

        if (!IsTraining)
        {

            // Setup  Game menu 
            Menus = UIManager.GetComponentInChildren<GameMenus>();
            Menus.Setup(this);
            Menus.ActionBox.Setup(PrimaryLevel, EventSystem, ConfigData.Configuration.UserSide);


            // Setup Selection Box
            Selector = SelectionBox.GetComponentInChildren<Selector>();
            Selector.Setup(PrimaryLevel, SelectionBox);
            // Setup input manager
            InputManager = new LevelInputManager(this, Selector);


            // Setup Squad Action Box
            if (ActivateAudio && Audio != null)
            {
                Audio.Setup(PlayMusic);
            }

            if (ConfigData.IsPlayingCampaign)
            {
                Menus.UpdateScore(ConfigData.GetUserProgressData().HumanWins, ConfigData.GetUserProgressData().BeeWins);
            }
            else
            {
                Menus.UpdateScore(ConfigData.GetUserProgressData().HumanFreePlayWins, ConfigData.GetUserProgressData().BeeFreePlayWins);
            }

            //TargetingMouseTexture = TargetingMouse.sprite.texture;
        }
        else
        {
            if (Audio != null)
            {
                Audio.gameObject.SetActive(false);
            }
        }


        if (!IsTraining && !UnlockCamera)
        {

            Vector2 cameraWorldUnitsSize = Utilities.ScreenPixelsToWorldUnits(new Vector2(MiniMapCamera.pixelWidth, MiniMapCamera.pixelHeight), Camera);
            Transform colliderContainer = Camera.transform.GetChild(0);
            colliderContainer.localScale = cameraWorldUnitsSize;
            Vector2 localizedPosition = DefaultCameraPosition + PrimaryLevel.GetPosition();
            Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);

            InputManager.MaintainScrollBoundary();
        }

        SetupLevels();

        float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
        Debug.Log($"It took {Math.Round(end, 2)} ms to set up the stage and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
    }
    /// <summary>
    /// Sets up the camera for the Primary Level once the primary level is ready for it
    /// </summary>
    public void SetupCamera()
    {
        Camera.orthographicSize = DefaultZoom;
        Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
        Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);
        InputManager.MaintainScrollBoundary();
        if ((OverrideUserSide == 1 || OverrideUserSide == 2) && OverrideUserSide != ConfigData.Configuration.UserSide)
        {
            ConfigData.SwapSides();
            Menus.ActionBox.Setup(PrimaryLevel, EventSystem, ConfigData.Configuration.UserSide);
        }

        MiniMapCamera.gameObject.SetActive(true);
        MiniMapCamera.orthographicSize = PrimaryLevel.Map.MiniMapCameraSize;
    }
    /// <summary>
    /// Sets up overrides for all the levels
    /// </summary>
    public void SetConfigOptionsAndOverrides()
    {
        if (TimeoutTime == 0)
        {
            TimeoutTime = int.MaxValue;
        }

        if (OverrideTimeScale == 0)
        {
            TimeScale = ConfigData.Configuration.TimeScale;
        }
        else
        {
            TimeScale = OverrideTimeScale;

        }
        if (GeneratedSquadCountOverride > 0)
        {
            PrimaryLevel.CurrentLevelOptions.EnemySquadGenerationCount = GeneratedSquadCountOverride;
        }
        if (PrimaryLevel.CurrentLevelOptions.EnemySquadGenerationCount > 0)
        {
            PrimaryLevel.CurrentLevelOptions.EnemySquadGenerationCount = Utilities.RandomInt(PrimaryLevel.CurrentLevelOptions.EnemySquadGenerationCount) + 1;
        }

        if (OverrideBeeShipTypes.Count > 0)
        {
            BeeShipTypes = OverrideBeeShipTypes;
        }
        else
        {
            BeeShipTypes = ConfigData.BeeShipTypes.ToList();
        }

        if (OverrideHumanShipTypes.Count > 0)
        {
            HumanShipTypes = OverrideHumanShipTypes;
        }
        else
        {
            HumanShipTypes = ConfigData.HumanShipTypes.ToList();
        }

        if (PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption == -1)
        {
            if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
            {
                BeeShipTypes = new List<string>() { BeeShipTypes[Utilities.RandomInt(BeeShipTypes.Count)] };
                Debug.Log($"The user has selected randomized enemy ship type: {BeeShipTypes[0]}");
            }
            else
            {
                HumanShipTypes = new List<string>() { HumanShipTypes[Utilities.RandomInt(HumanShipTypes.Count)] };
                Debug.Log($"The user has selected randomized enemy ship type: {HumanShipTypes[0]}");
            }

        }
        else if (PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption == 0)
        {
            //Debug.Log($"The map does not have a singular enemy ship type");
            if (OverrideBeeShipTypes.Count > 0)
            {
                BeeShipTypes = OverrideBeeShipTypes;
            }
            else
            {
                BeeShipTypes = ConfigData.BeeShipTypes.ToList();
            }

            if (OverrideHumanShipTypes.Count > 0)
            {
                HumanShipTypes = OverrideHumanShipTypes;
            }
            else
            {
                HumanShipTypes = ConfigData.HumanShipTypes.ToList();
            }
        }
        else
        {
            if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
            {
                BeeShipTypes = new List<string>() { BeeShipTypes[PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption - 1] };
                Debug.Log($"The user has selected enemy ship type: {BeeShipTypes[0]}");
            }
            else
            {
                HumanShipTypes = new List<string>() { HumanShipTypes[PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption - 1] };
                Debug.Log($"The user has selected enemy ship type: {HumanShipTypes[0]}");
            }
        }
    }

    // Update is called once per frame
    new void Update()
    {
        base.Update();
        if (!IsTrainingNueralNetwork)
        {
            Time.timeScale = TimeScale;
            if (!IsTrainingHiveMind && IsFinalized)
            {
                InputManager.Update();
            }
        }

        if (IsDebugging && FixedUpdates > 1000)
        {
            DebugLogger();
        }
    }
    void FixedUpdate()
    {
        FixedUpdates++;
    }
}
