using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.Pool;
using Assets.Scripts.UIComponents;
using System.Security.Cryptography;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Server;

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
    /// How many obstacle lists there are
    /// </summary>
    public int ObstacleListCount;
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
    public List<ConfigData.ShipTypes> OverrideBeeShipTypes = new List<ConfigData.ShipTypes> { };
    /// <summary>
    /// Only allows Human ship types as specified here, unless it's empty
    /// </summary>
    public List<ConfigData.ShipTypes> OverrideHumanShipTypes = new List<ConfigData.ShipTypes> { };
    /// <summary>
    /// Only allows Hive Mind strats of the types specified here, unless it's empty. Gets converted to OverridenStrats which has the enum of every strategy type
    /// </summary>
    public List<ConfigData.CommandTypes> OverrideStrats = new List<ConfigData.CommandTypes> { };
    /// <summary>
    /// The set of positions for each level depending on the number of levels on the stage
    /// </summary>
    public Dictionary<int, Vector2[]> LevelLayouts = new Dictionary<int, Vector2[]>
    {
        {1, new Vector2[] { Vector2.zero, Vector2.zero } },
        {2, new Vector2[] { new Vector2(-756, 0), new Vector2(756, 0) } },
        {4, new Vector2[] { new Vector2(-756, 756), new Vector2(756, 756), new Vector2(-756, -756), new Vector2(756, -756) } },
        {16, new Vector2[] {
            new Vector2(-756, 756), new Vector2(-2268, 756), new Vector2(756, 756), new Vector2(2268, 756),
            new Vector2(-756, 2268), new Vector2(-2268, 2268), new Vector2(756, 2268), new Vector2(2268, 2268),
            new Vector2(-756, -756), new Vector2(-2268, -756), new Vector2(756, -756), new Vector2(2268, -756),
            new Vector2(-756, -2268), new Vector2(-2268, -2268), new Vector2(756, -2268), new Vector2(2268, -2268)} 
        },
    };
    /// <summary>
    /// All the clearances for all the ships, calculated in levels dynamically when needed but shared between all the levels
    /// </summary>
    public Dictionary<ConfigData.ShipTypes, int> ShipClearances = new Dictionary<ConfigData.ShipTypes, int>();
    /// <summary>
    /// The sprite used for user ship vision to clear the fog of war
    /// </summary>
    public Sprite VisonSprite;
    /// <summary>
    /// Holds all the entity prefabs for the game (Ships, projectiles, Obstacles, Asteroids, etc.)
    /// </summary>
    public Prefabs Prefabs;
    /// <summary>
    /// The manager of all pooled objects like ships, projectiles. and commands
    /// </summary>
    public Pool Pool;
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
    public List<ConfigData.ShipTypes> BeeShipTypes = new List<ConfigData.ShipTypes>();
    /// <summary>
    /// The current Human ship types available for the levels
    /// </summary>
    public List<ConfigData.ShipTypes> HumanShipTypes = new List<ConfigData.ShipTypes>();
    /// <summary>
    /// How many fixed updates have passed since the stage spawned
    /// </summary>
    public int FixedUpdates;
    /// <summary>
    /// The time in seconds when the stage started up [debugging]
    /// </summary>
    public float StartTime;
    /// <summary>
    /// Whether or not there are any asteroids on any levels in this stage
    /// </summary>
    public bool HasAsteroids;


    public int __HivemindCommands, __LevelTimeouts;
    // DEBUG VARIABLES
    public static int __TotalRequests;
    public static double __TotalLatency, __AverageLatency, __TotalLength, __AverageLength;
    public void DebugLogger()
    {
        __TotalRequests = ConfigData.__TotalRequests;
        __TotalLatency = ConfigData.__TotalLatency;
        __AverageLatency = ConfigData.__AverageLatency;
        __TotalLength = ConfigData.__TotalLength;
        __AverageLength = ConfigData.__AverageLength;
    }



    // Start is called before the first frame update
    new void Start()
    {
        StartTime = Time.realtimeSinceStartup;
        Debug.Log($"Start level stage");
        Name = "Level";
        base.Start();
    }
    int _spawn_i;
    Level _spawn_level;
    /// <summary>
    /// Spawns the other levels on this stage
    /// </summary>
    private void SpawnLevels()
    {
        Debug.Log($"Spawning stage levels");
        //transform.position = LevelLayouts[LevelCount][0];
        for (_spawn_i = 0; _spawn_i < LevelCount; _spawn_i++)
        {
            _spawn_level = Instantiate(Prefabs.LevelPrefab.gameObject, transform.parent).GetComponent<Level>();
            if (_spawn_i == 0)
            {
                PrimaryLevel = _spawn_level;
            }
            _spawn_level.transform.parent = transform;
            _spawn_level.gameObject.SetActive(true);
            _spawn_level.transform.localPosition = LevelLayouts[LevelCount][_spawn_i];
            Levels.Add(_spawn_level);

        }
    }
    int _setup_i;
    private void SetupLevels()
    {
        for (_setup_i = 0; _setup_i < Levels.Count; _setup_i++)
        {
            Levels[_setup_i].Setup(this, $"Level - #{_setup_i}");
        }
    }

    float _finalize_end;
    Vector2 _finalize_cameraWorldUnitsSize, _finalize_localizedPosition;
    Transform _finalize_colliderContainer;
    protected override void FinalizeSceneWithUserData()
    {
        Debug.Log($"Finalize scene");


        base.FinalizeSceneWithUserData();

        if (IsMainScene && LevelCount > 0)
        {
            Prefabs.LoadConversions();
            Pool.Setup(this);
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
            if (ActivateAudio)
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
            if (ActivateAudio)
            {
                Audio.gameObject.SetActive(false);
            }
        }


        if (!IsTraining && !UnlockCamera)
        {

            _finalize_cameraWorldUnitsSize = Utilities.ScreenPixelsToWorldUnits(new Vector2(MiniMapCamera.pixelWidth, MiniMapCamera.pixelHeight), Camera);
            _finalize_colliderContainer = Camera.transform.GetChild(0);
            _finalize_colliderContainer.localScale = _finalize_cameraWorldUnitsSize;
            _finalize_localizedPosition = DefaultCameraPosition + PrimaryLevel.GetPosition();
            Camera.transform.position = new Vector3(_finalize_localizedPosition.x, _finalize_localizedPosition.y, -10);

            InputManager.MaintainScrollBoundary();
        }

        SetupLevels();

        if (HasAsteroids)
        {
            Pool.FillAsteroidPool();
        }

        _finalize_end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
        Debug.Log($"It took {Math.Round(_finalize_end, 2)} ms to set up the stage and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
    }
    Vector2 _camera_localizedPosition;
    /// <summary>
    /// Sets up the camera for the Primary Level once the primary level is ready for it
    /// </summary>
    public void SetupCamera()
    {
        Camera.orthographicSize = DefaultZoom;
        _camera_localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
        Camera.transform.position = new Vector3(_camera_localizedPosition.x, _camera_localizedPosition.y, -10);
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
                BeeShipTypes = new List<ConfigData.ShipTypes>() { BeeShipTypes[Utilities.RandomInt(BeeShipTypes.Count)] };
                Debug.Log($"The user has selected randomized enemy ship type: {BeeShipTypes[0]}");
            }
            else
            {
                HumanShipTypes = new List<ConfigData.ShipTypes>() { HumanShipTypes[Utilities.RandomInt(HumanShipTypes.Count)] };
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
                BeeShipTypes = new List<ConfigData.ShipTypes>() { BeeShipTypes[PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption - 1] };
                Debug.Log($"The user has selected enemy ship type: {BeeShipTypes[0]}");
            }
            else
            {
                HumanShipTypes = new List<ConfigData.ShipTypes>() { HumanShipTypes[PrimaryLevel.CurrentLevelOptions.EnemyShipTypeOption - 1] };
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
            //Time.timeScale = TimeScale;
            if (!IsTrainingHiveMind && IsFinalized)
            {
                InputManager.Update();
            }
        }

        if (IsDebugging && FixedUpdates > 1000)
        {
            DebugLogger();
            Pool.DebugLogger();
        }
    }
    void FixedUpdate()
    {
        FixedUpdates++;
    }
}
