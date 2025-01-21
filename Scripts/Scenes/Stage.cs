using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UIComponents;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using UnityEngine;

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
    /// The set of positions for each level depending on the number of levels on the stage
    /// </summary>
    public Dictionary<int, Vector2[]> LevelLayouts = new Dictionary<int, Vector2[]>
    {
        {1, new Vector2[] { new Vector2(0, 0), new Vector2(0, 0) } },
        {2, new Vector2[] { new Vector2(-756, 0), new Vector2(756, 0) } },
        {4, new Vector2[] { new Vector2(-756, 756), new Vector2(756, 756), new Vector2(-756, -756), new Vector2(756, -756) } },
    };
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
    /// The main level that accepts user interaction
    /// </summary>
    public Level PrimaryLevel;
    /// <summary>
    /// All the levels that this stage has spawned
    /// </summary>
    public List<Level> Levels;
    /// <summary>
    /// Only allows Bee ship types as specified here, unless it's empty
    /// </summary>
    public List<string> OverrideBeeShipTypes = new List<string> { };
    /// <summary>
    /// Only allows Human ship types as specified here, unless it's empty
    /// </summary>
    public List<string> OverrideHumanShipTypes = new List<string> { };
    /// <summary>
    /// The current Bee ship types available for the levels
    /// </summary>
    public List<string> BeeShipTypes = new List<string>();
    /// <summary>
    /// The current Human ship types available for the levels
    /// </summary>
    public List<string> HumanShipTypes = new List<string>();






    // Start is called before the first frame update
    new void Start()
    {
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
            nextLevel.SetActive(true);
            nextLevel.transform.position = LevelLayouts[LevelCount][i];
            Levels.Add(level);

        }
    }
    private void SetupLevels()
    {
        for (int i = 0; i < Levels.Count; i++)
        {
            Levels[i].Setup(this);
        }
    }
    protected override void FinalizeSceneWithUserData()
    {
        Debug.Log($"Finalize scene");
        //StartTime = Time.realtimeSinceStartup;


        base.FinalizeSceneWithUserData();

        if (IsMainScene && LevelCount > 0)
        {
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
            Vector2 localizedPosition = PrimaryLevel.DefaultCameraPosition + PrimaryLevel.GetPosition();
            Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);

            InputManager.MaintainScrollBoundary();
        }

        SetupLevels();

        //float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
        //Debug.Log($"It took {Math.Round(end, 2)} ms to set up the level and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
    }
    /// <summary>
    /// Sets up the camera for the Primary Level once the primary level is ready for it
    /// </summary>
    public void SetupCamera()
    {
        Camera.orthographicSize = PrimaryLevel.DefaultZoom;
        Vector2 localizedPosition = PrimaryLevel.DefaultCameraPosition + (Vector2)transform.position;
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
            Debug.Log($"The map does not have a singular enemy ship type");
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
    }
}
