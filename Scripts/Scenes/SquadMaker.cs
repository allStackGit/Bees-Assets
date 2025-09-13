
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using Assets.Scripts.UIComponents;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using TMPro;

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Scenes
{
    public class SquadMaker : Scene
    {
        public int Side;

        public GameObject
            BargeDragIcon, BeaconDragIcon, CarrierDragIcon, CruiserDragIcon, DreadnoughtDragIcon, DroneDragIcon,
            FactoryDragIcon, FireBargeDragIcon, FlagshipDragIcon, FrigateDragIcon, GunshipDragIcon, ScoutDragIcon,
            StrikerDragIcon, WarpGateDragIcon,

            BeehiveDragIcon, BumblebeeDragIcon, CarpenterBeeDragIcon, HoneybeeDragIcon, HornetDragIcon, LeafcutterDragIcon, QueenDragIcon,
            WaspDragIcon, YellowJacketDragIcon,

            BargeShipIcon, BeaconShipIcon, CarrierShipIcon, CruiserShipIcon, DreadnoughtShipIcon, DroneShipIcon,
            FactoryShipIcon, FireBargeShipIcon, FlagshipShipIcon, FrigateShipIcon, GunshipShipIcon, ScoutShipIcon,
            StrikerShipIcon, WarpGateShipIcon,

            BeehiveShipIcon, BumblebeeShipIcon, CarpenterBeeShipIcon, HoneybeeShipIcon, HornetShipIcon, LeafcutterShipIcon, QueenShipIcon,
            WaspShipIcon, YellowJacketShipIcon,

            BargeFleetLabel, BeaconFleetLabel, CarrierFleetLabel, CruiserFleetLabel, DreadnoughtFleetLabel, DroneFleetLabel,
            FactoryFleetLabel, FireBargeFleetLabel, FlagshipFleetLabel, FrigateFleetLabel, GunshipFleetLabel, ScoutFleetLabel,
            StrikerFleetLabel, WarpGateFleetLabel,

            BeehiveFleetLabel, BumblebeeFleetLabel, CarpenterBeeFleetLabel, HoneybeeFleetLabel, HornetFleetLabel, LeafcutterFleetLabel, QueenFleetLabel,
            WaspFleetLabel, YellowJacketFleetLabel,

            SquadMakerSupplyCapacityLabel, ChosenSquadsSupplyCapacityLabel, Tooltip, TooltipText, ColorPicker, SavedSquadList,
            ChosenSquadList, SavedSquadPrefab, ChosenSquadPrefab,

            SquadActionBox, DeadShipBox, DropZone, DropBox, DragStatusBox, ShipInfoBox, ShipInfoBoxTitle, ShipInfoBoxDetails, ShipInfoBoxIcon, SquadInfoBox,
            SquadInfoBoxTitle, SquadInfoBoxDetails, SquadInfoBoxIcon, ShipStatsBox, ShipStatsBoxDetails, SquadNameInput, ShipNameInput,
            SquadShipCount, SquadShipCountLabel, SquadColorLabel, SquadColorPickerButton, NextButton, StartButton, FogOfWarLabel, FogOfWarDropdown, MiningLabel,
            MiningDropdown, EnemyReinforcementsLabel, EnemyReinforcementsDropdown, MapLabel, MapDropdown, AsteroidsLabel, AsteroidsDropdown, ObstaclesLabel, ObstaclesDropdown,
            OpposingForceLabel, OpposingForcePresetDropdown, ChosenEnemyShipTypeLabel, ChosenEnemyShipTypesDropdown, LevelTitleContainer, LevelDetailsContainer, ChooseLevelLabel, BuildButton, BuildPopup, Minerals, BuildCost, BuildButtonHighlight, BuildButtonMessage;

        public Dialogue DeleteSquadConfirmation, ClearSquadConfirmation, LoadSquadConfirmation, ChooseSquadConfirmation, UnchooseSquadConfirmation, OverCapacityAlert, NoChosenSquadsAlert,
            ChoosingUnsavedSquadAlert, ChoosingDeadSquadAlert, GoBackConfirmation, SquadSavingStatus, CannotDuplicateSquad;

        public Sprite
            BargeSprite, BeaconSprite, CarrierSprite, CruiserSprite, DreadnoughtSprite, DroneSprite, FactorySprite, FireBargeSprite, FlagshipSprite, FrigateSprite,
            GunshipSprite, ScoutSprite, StrikerSprite, WarpGateSprite,

            BeehiveSprite, BumblebeeSprite, CarpenterBeeSprite, HoneybeeSprite, HornetSprite, LeafcutterSprite, QueenSprite,
            WaspSprite, YellowJacketSprite;

        public Sprite
            BargeGameSprite, CarrierGameSprite, CruiserGameSprite, CruiserCannonGameSprite, DreadnoughtGameSprite, DroneGameSprite, FactoryGameSprite, FactoryAnimationSprite, FireBargeGameSprite, FlagshipGameSprite, FrigateGameSprite,
            GunshipGameSprite, ScoutGameSprite, StrikerGameSprite, WarpGateGameSprite, WarpGateAnimationSprite, WarpGateAnimationLoopSprite;

        public Sprite BargeRemainsSprite, CarrierRemainsSprite, CruiserRemainsSprite, DreadnoughtRemainsSprite, DroneRemainsSprite, FactoryRemainsSprite, FlagshipRemainsSprite,
            FrigateRemainsSprite, GunshipRemainsSprite, ScoutRemainsSprite, StrikerRemainsSprite, WarpGateRemainsSprite;

        public Canvas DragCanvas;
        public Vector2 TooltipOffset, ShipStatsBoxOffset, ScreenScaleFactor, ReferenceScreenSize;
        public SquadActionBox ActionBox = null;
        public TMP_Dropdown LevelDropdown;
        public TMP_Text LevelTitle, LevelDetails;
        public bool IsRandomizedOpposingSide;
        public string CurrentFormation = "Line";
        public Dictionary<ConfigData.ShipTypes, int> ShipsBeingBuilt = new Dictionary<ConfigData.ShipTypes, int>
        {
            { ConfigData.ShipTypes.Barge, 0 },
            { ConfigData.ShipTypes.Beehive, 0 },
            { ConfigData.ShipTypes.Bumblebee, 0 },
            { ConfigData.ShipTypes.CarpenterBee, 0 },
            { ConfigData.ShipTypes.Carrier, 0 },
            { ConfigData.ShipTypes.Cruiser, 0 },
            { ConfigData.ShipTypes.Dreadnought, 0 },
            { ConfigData.ShipTypes.Factory, 0 },
            { ConfigData.ShipTypes.FireBarge, 0 },
            { ConfigData.ShipTypes.Flagship, 0 },
            { ConfigData.ShipTypes.Frigate, 0 },
            { ConfigData.ShipTypes.Gunship, 0 },
            { ConfigData.ShipTypes.Honeybee, 0 },
            { ConfigData.ShipTypes.Hornet, 0 },
            { ConfigData.ShipTypes.Leafcutter, 0 },
            { ConfigData.ShipTypes.Queen, 0 },
            { ConfigData.ShipTypes.Scout, 0 },
            { ConfigData.ShipTypes.WarpGate, 0 },
            { ConfigData.ShipTypes.Wasp, 0 },
            { ConfigData.ShipTypes.YellowJacket, 0 }
        };
        public TMP_Text TotalBuildCostText, MineralsText;
        public int TotalBuildCost = 0;




        private Dictionary<ConfigData.ShipTypes, GameObject> _dragIconTypes = new Dictionary<ConfigData.ShipTypes, GameObject>();
        private Dictionary<ConfigData.ShipTypes, Sprite> _spriteTypes = new Dictionary<ConfigData.ShipTypes, Sprite>();
        private Dictionary<ConfigData.ShipTypes, List<Sprite>> _shipPartSprites = new Dictionary<ConfigData.ShipTypes, List<Sprite>>();
        private Dictionary<ConfigData.ShipTypes, List<Sprite>> _shipRemainsSprites = new Dictionary<ConfigData.ShipTypes, List<Sprite>>();
        private Dropper _dropper;
        private List<GameObject> _deadShipBoxes = new List<GameObject>();
        private List<SavedSquad> _chosenSquads = new List<SavedSquad>();
        private List<FleetShip> _fleetList = null;
        private List<ConfigData.ShipTypes> _shipTypes = new List<ConfigData.ShipTypes>();
        private SavedSquad _currentSquad = null;
        private SavedSquad _squadToLoad = null;
        private SavedSquad _squadToChoose = null;
        private SavedSquad _squadToUnchoose = null;
        private FleetShip _currentShipInfo;
        private Color _squadColor = ConfigData.UnsetColor;
        private ColorPicker _colorPicker;
        private string _nameText = "";
        private bool _showShipInfo = false;
        private bool _showSquadInfo = false;
        private bool _showTooltip = false;
        private bool _showShipStatsBox = false;
        private bool _singleClick = false;
        private bool _doubleClick = false;
        private bool _startingLevel = false;
        private int _chosenOpposingForceOption;
        private int _enemySquadGenerationCount;
        private int _chosenObstacleOption = -1;
        private int _chosenAsteroidsOption = -1;
        private int _chosenMapOption = -1;
        private int _chosenFogOfWarOption = -1;
        private int _chosenMiningOption = -1;
        private int _chosenEnemyReinforcementsOption = -1;
        private int _chosenEnemyShipTypes = -1;
        private string _nextScene = "";
        private LevelOptions _chosenLevel;
        private Dictionary<int, LevelOptions> _levelOptionIndexesToLevels = new Dictionary<int, LevelOptions>();
        private int _squadListOriginalScrollHeight, _squadListOptionsScrollHeight, _squadListLevelScrollHeight;
        int _capacity;


        public bool HasActionBox => ActionBox != null;
        public bool HasColorPicker => _colorPicker != null;
        public bool HasCurrentSquad => _currentSquad != null;





        // Setup methods
        private new void Start()
        {
            Name = "Squad Maker";
            base.Start();
            //Debug.Log("Starting squad maker");
            //Debug.Log($"GameObject: {gameObject}, TimeScale: {Time.timeScale}");
            InvokeRepeating(nameof(UpdateDimensions), 1, 1f);
        }
        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();
            Setup();


            ConfigData.CurrentShips.ReplaceDeadSquadShips(ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign);
            _fleetList = ConfigData.CurrentShips.GetAvailableShips();
            SetupFleetList();
            SetupSavedSquadsList();

            // turn squad labels red for all squads that still have dead ships
            ConfigData.CurrentShips.GetSavedSquadsBySide(Side).ForEach((squad) =>
            {
                if (squad.HasDeadShips)
                {
                    //Debug.Log($"{squad.Name} still has dead ships");
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}"));
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>());
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color);
                    GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
                }
            });
            //Debug.Log("Finalized the page");
        }
        private void Setup()
        {
            //Debug.Log($"Squad Maker Setup called");
            ConfigData.ChooseRandomLevel = false;
            // Universal pre setup
            _dropper = new Dropper(this);
            Side = ConfigData.SquadMakerSide;
            //ConfigData.SetupSceneManagement(SceneManagement.GetComponent<SceneManagement>());
            ScreenScaleFactor = new Vector2(ConfigData.ScreenWidth / ReferenceScreenSize.x, ConfigData.ScreenHeight / ReferenceScreenSize.y);

            //if (Side == ConfigData.Configuration.SquadMakerFirstSide)
            //{
            //    ConfigData.SquadsChosenForLevel.Clear();
            //    NextButton.SetActive(true);
            //    StartButton.SetActive(false);
            //}
            //else
            //{
            //    NextButton.SetActive(false);
            //    StartButton.SetActive(true);
            //}

            _squadListOriginalScrollHeight = 435 + 238;
            _squadListOptionsScrollHeight = _squadListOriginalScrollHeight - 238;
            _squadListLevelScrollHeight = _squadListOriginalScrollHeight - 395;

            // Make Dialogues
            DeleteSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.DeleteSquadConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { DeleteCurrentSquad });

            ClearSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.ClearSquadConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ClearChanges });

            LoadSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.LoadSquadConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { LoadSquad });

            ChooseSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.ChooseSquadConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ChooseSquad });

            UnchooseSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.UnchooseSquadConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { UnchooseSquad });

            GoBackConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.GoBackConfirmation,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { GoBack });

            OverCapacityAlert = new Alert(DialoguePrefab, ConfigData.Configuration.OverCapacityAlertTitle, ConfigData.Configuration.OverCapacityAlert,
                ConfigData.Configuration.OK);

            NoChosenSquadsAlert = new Alert(DialoguePrefab, ConfigData.Configuration.NoChosenSquadsAlertTitle, ConfigData.Configuration.NoChosenSquadsAlert,
                ConfigData.Configuration.OK);

            ChoosingUnsavedSquadAlert = new Alert(DialoguePrefab, ConfigData.Configuration.ChoosingUnsavedSquadAlertTitle, ConfigData.Configuration.ChoosingUnsavedSquadAlert,
                ConfigData.Configuration.OK);

            ChoosingDeadSquadAlert = new Alert(DialoguePrefab, ConfigData.Configuration.ChoosingDeadSquadAlertTitle, ConfigData.Configuration.ChoosingDeadSquadAlert,
               ConfigData.Configuration.OK);

            SquadSavingStatus = new Dialogue(DialoguePrefab, ConfigData.Configuration.SquadSavingStatusAlertTitle, ConfigData.Configuration.SquadSavingStatusAlert,
               new List<string>(), new List<UnityAction>());

            CannotDuplicateSquad = new Alert(DialoguePrefab, ConfigData.Configuration.CannotDuplicateSquadAlertTitle, ConfigData.Configuration.CannotDuplicateSquadAlert,
              ConfigData.Configuration.OK);

            // setup for Bees and Humans
            if (Side == ConfigData.Configuration.BeeSide)
            {
                SetupForBees();
            }
            else if (Side == ConfigData.Configuration.HumanSide)
            {
                SetupForHumans();
            }
            else
            {
                Debug.LogError($"Side ({Side}) does not match Bee Side ({ConfigData.Configuration.BeeSide}) or Human Side ({ConfigData.Configuration.HumanSide})");
            }
            if (Side != ConfigData.Configuration.UserSide)
            {
                SetupForOpposingSide();
            }
            else if (IsRandomizedOpposingSide)
            {
                SkipOpposingSideSetup();
            }

            _capacity = ConfigData.StartingSettings.SupplyCapacity[Side - 1];
            // Post setup
            //Debug.Log("Post setup");
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                SetupForCampaign();
            }
            UpdateSquadMakerSupplyLabel();
            UpdateChosenSquadsSupplyLabel();
            UpdateSquadShipCounter();
            SetupLevelDropdown();



            //Debug.Log($"{_squadListOriginalScrollHeight}, {_squadListOptionsScrollHeight}, {_squadListLevelScrollHeight}");

        }
        private void SetupForCampaign()
        {
            //Debug.Log($"Setting up for campaign");
            int i = 0;
            ConfigData.GetCampaignLevelData().GetLevels().Where((level) => level.Side != Side).ToList().ForEach((level) =>
            {
                //Debug.Log($"Adding option for {level} -> #{i}");
                _levelOptionIndexesToLevels[i] = level;
                i++;
            });
            Debug.Log($"User is on level #{ConfigData.UserProgressData.GetCurrentLevel()}");

            LoadLevel(ConfigData.UserProgressData.GetCurrentLevel());

            if (ConfigData.UserProgressData.MinedTSV > 0 && ConfigData.CurrentShips.GetAliveShipsOfType(ConfigData.ShipTypes.Factory).Count > 0)
            {
                BuildButton.SetActive(true);
                SetupBuildInterface();

                if (!ConfigData.UserProgressData.HasSeenBuildInterface)
                {
                    ShowBuildButtonMessage();
                }
            }
        }
        private void SetupLevelDropdown()
        {
            //Debug.Log($"Setting up level dropdown");
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                LevelDropdown.gameObject.SetActive(false);
                ChooseLevelLabel.SetActive(false);
            }
            else
            {
                int i = 2;
                ConfigData.GetLevelData().GetLevels().Where((level) => level.Side != Side).ToList().ForEach((level) =>
                {
                    //Debug.Log($"Adding option for {level} -> #{i}");
                    LevelDropdown.options.Add(new TMP_Dropdown.OptionData(level.Name));
                    _levelOptionIndexesToLevels[i] = level;
                    i++;
                });
                LevelDropdown.SetValueWithoutNotify(1);
            }

        }
        private void SetupForOpposingSide()
        {

            // Hide the level options and extend the squad list size
            ToggleLevelDetails(false);
            ToggleLevelOptions(false);
        }
        public void LoadLevel(int levelIndex)
        {
            //levelIndex = 11;
            _chosenLevel = _levelOptionIndexesToLevels[levelIndex];
            //Debug.Log($"Loading level {_chosenLevel}");
            LevelTitle.text = $"Level: {_chosenLevel.Name}";
            LevelDetails.text = _chosenLevel.GetLevelDetails();
            _capacity = _chosenLevel.SupplyCapacity;
            UpdateChosenSquadsSupplyLabel();
            ToggleLevelOptions(false); // hide the level options
            ToggleLevelDetails(true); // show the level details
        }
        private void SkipOpposingSideSetup()
        {
            NextButton.SetActive(false);
            StartButton.SetActive(true);

            // Hide the "Choose Opposing Force" options and extend the squad list size
            OpposingForceLabel.SetActive(false);
            OpposingForcePresetDropdown.SetActive(false);
            RectTransform squadListRect = ChosenSquadList.transform.parent.parent.GetComponent<RectTransform>();
            squadListRect.sizeDelta = new Vector2(squadListRect.sizeDelta.x, squadListRect.sizeDelta.y + 200);
        }
        private void SetupForBees()
        {
            //Debug.Log($"Setting up for Bees!");
            ActionBox = null;

            _dragIconTypes.Add(ConfigData.ShipTypes.Beehive, BeehiveDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Bumblebee, BumblebeeDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.CarpenterBee, CarpenterBeeDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Honeybee, HoneybeeDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Hornet, HornetDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Leafcutter, LeafcutterDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Queen, QueenDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Wasp, WaspDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.YellowJacket, YellowJacketDragIcon);

            _spriteTypes.Add(ConfigData.ShipTypes.Beehive, BeehiveSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Bumblebee, BumblebeeSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.CarpenterBee, CarpenterBeeSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Honeybee, HoneybeeSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Hornet, HornetSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Leafcutter, LeafcutterSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Queen, QueenSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Wasp, WaspSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.YellowJacket, YellowJacketSprite);

            ActionBox = SquadActionBox.GetComponent<SquadActionBox>();
            ActionBox.Setup(this, EventSystem, ConfigData.Configuration.BeeSide);

            _shipTypes = ConfigData.StartingSettings.BeeShipTypes;

            StrikerFleetLabel.transform.parent.gameObject.SetActive(false);
            DroneFleetLabel.transform.parent.gameObject.SetActive(false);
            BeaconFleetLabel.transform.parent.gameObject.SetActive(false);
            //SquadColorLabel.SetActive(false);
            SquadColorPickerButton.SetActive(false);

            TMP_Dropdown dropdown = ChosenEnemyShipTypesDropdown.GetComponent<TMP_Dropdown>();

            ConfigData.HumanShipTypes.ToList().ForEach(ship =>
            {
                //Debug.Log("Setting drop down option");
                dropdown.options.Add(new TMP_Dropdown.OptionData($"{Utilities.ConvertShipTypeToName[ship]}"));
            });


        }
        private void SetupForHumans()
        {
            //Debug.Log("Setting up for Humans!");
            _colorPicker = ColorPicker.GetComponent<ColorPicker>();

            _dragIconTypes.Add(ConfigData.ShipTypes.Barge, BargeDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Beacon, BeaconDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Carrier, CarrierDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Cruiser, CruiserDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Dreadnought, DreadnoughtDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Drone, DroneDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Factory, FactoryDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.FireBarge, FireBargeDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Flagship, FlagshipDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Frigate, FrigateDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Gunship, GunshipDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Scout, ScoutDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.Striker, StrikerDragIcon);
            _dragIconTypes.Add(ConfigData.ShipTypes.WarpGate, WarpGateDragIcon);

            _spriteTypes.Add(ConfigData.ShipTypes.Barge, BargeSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Beacon, BeaconSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Carrier, CarrierSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Cruiser, CruiserSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Dreadnought, DreadnoughtSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Drone, DroneSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Factory, FactorySprite);
            _spriteTypes.Add(ConfigData.ShipTypes.FireBarge, FireBargeSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Flagship, FlagshipSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Frigate, FrigateSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Gunship, GunshipSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Scout, ScoutSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.Striker, StrikerSprite);
            _spriteTypes.Add(ConfigData.ShipTypes.WarpGate, WarpGateSprite);


            _shipPartSprites[ConfigData.ShipTypes.Barge] = new List<Sprite> { BargeGameSprite };
            //_shipPartSprites[ConfigData.ShipTypes.Beacon] = new List<Sprite> { BeaconGameSprite }; // no beacon because we won't be caching sprites for beacons
            _shipPartSprites[ConfigData.ShipTypes.Carrier] = new List<Sprite> { CarrierGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Cruiser] = new List<Sprite> { CruiserGameSprite, CruiserCannonGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Dreadnought] = new List<Sprite> { DreadnoughtGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Drone] = new List<Sprite> { DroneGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Factory] = new List<Sprite> { FactoryGameSprite, FactoryAnimationSprite };
            _shipPartSprites[ConfigData.ShipTypes.FireBarge] = new List<Sprite> { FireBargeGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Flagship] = new List<Sprite> { FlagshipGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Frigate] = new List<Sprite> { FrigateGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Gunship] = new List<Sprite> { GunshipGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Scout] = new List<Sprite> { ScoutGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.Striker] = new List<Sprite> { StrikerGameSprite };
            _shipPartSprites[ConfigData.ShipTypes.WarpGate] = new List<Sprite> { WarpGateGameSprite, WarpGateAnimationSprite, WarpGateAnimationLoopSprite };


            // No bee sprites because those don't change colors

            // Same thing as above but for ship remains animations
            _shipRemainsSprites[ConfigData.ShipTypes.Barge] = new List<Sprite> { BargeRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Carrier] = new List<Sprite> { CarrierRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Cruiser] = new List<Sprite> { CruiserRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Dreadnought] = new List<Sprite> { DreadnoughtRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Drone] = new List<Sprite> { DroneRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Factory] = new List<Sprite> { FactoryRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Flagship] = new List<Sprite> { FlagshipRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Frigate] = new List<Sprite> { FrigateRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Gunship] = new List<Sprite> { GunshipRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Scout] = new List<Sprite> { ScoutRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.Striker] = new List<Sprite> { StrikerRemainsSprite };
            _shipRemainsSprites[ConfigData.ShipTypes.WarpGate] = new List<Sprite> { WarpGateRemainsSprite };

            ActionBox = SquadActionBox.GetComponent<SquadActionBox>();
            ActionBox.Setup(this, EventSystem, ConfigData.Configuration.HumanSide);

            _shipTypes = ConfigData.StartingSettings.HumanShipTypes;

            TMP_Dropdown dropdown = ChosenEnemyShipTypesDropdown.GetComponent<TMP_Dropdown>();

            ConfigData.BeeShipTypes.ToList().ForEach(ship =>
            {
                //Debug.Log("Setting drop down option");
                dropdown.options.Add(new TMP_Dropdown.OptionData($"{Utilities.ConvertShipTypeToName[ship]}"));
            });
            //Debug.Log("End of human setup");
        }
        private void UpdateDimensions()
        {
            //Debug.Log("Updating dimensions");
            if (Screen.width != ConfigData.ScreenWidth || Screen.height != ConfigData.ScreenHeight)
            {
                ConfigData.ScreenWidth = Screen.width;
                ConfigData.ScreenHeight = Screen.height;
                //Debug.Log("Updated the base world point");
                ScreenScaleFactor = new Vector2(ConfigData.ScreenWidth / ReferenceScreenSize.x, ConfigData.ScreenHeight / ReferenceScreenSize.y);
                Debug.Log($"The screen scale factor is {ScreenScaleFactor} and one world unit is {Utilities.WorldUnitsToScreenPixels(Vector2.one, Camera)} pixels in size");
                if (HasColorPicker)
                {
                    _colorPicker.SetScreenScaleFactor();
                }
                List<DragIcon> dragIcons = GetDropper().GetDragIcons();
                if (_currentSquad != null)
                {
                    bool hasChanged = _currentSquad.HasChanged;
                    _currentSquad.GetSquadShips().Clear();
                    List<DragIcon> icons = dragIcons.ToList();
                    //_dragIcons.Clear();
                    icons.ForEach((icon) =>
                    {
                        icon.Reposition(icon.Position, null);
                    });

                    _currentSquad.SetChanged(hasChanged);
                }
            }
        }
        private void SetupFleetList()
        {
            //Debug.Log($"Setting up the fleet list, {ConfigData.StartingSettings.HumanShipTypes.Count}");

            // loop through all ship types
            _shipTypes.ForEach(type =>
            {
                //Debug.Log($"Getting fleet ships for {type}");
                GameObject shipLabel = null;
                switch (type)
                {
                    case ConfigData.ShipTypes.Barge:
                        shipLabel = BargeFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Beacon:
                        shipLabel = BeaconFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Carrier:
                        shipLabel = CarrierFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Cruiser:
                        shipLabel = CruiserFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Dreadnought:
                        shipLabel = DreadnoughtFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Drone:
                        shipLabel = DroneFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Factory:
                        shipLabel = FactoryFleetLabel;
                        break;
                    case ConfigData.ShipTypes.FireBarge:
                        shipLabel = FireBargeFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Flagship:
                        shipLabel = FlagshipFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Frigate:
                        shipLabel = FrigateFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Gunship:
                        shipLabel = GunshipFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Scout:
                        shipLabel = ScoutFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Striker:
                        shipLabel = StrikerFleetLabel;
                        break;
                    case ConfigData.ShipTypes.WarpGate:
                        shipLabel = WarpGateFleetLabel;
                        break;


                    case ConfigData.ShipTypes.Beehive:
                        shipLabel = BeehiveFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Bumblebee:
                        shipLabel = BumblebeeFleetLabel;
                        break;
                    case ConfigData.ShipTypes.CarpenterBee:
                        shipLabel = CarpenterBeeFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Honeybee:
                        shipLabel = HoneybeeFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Hornet:
                        shipLabel = HornetFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Leafcutter:
                        shipLabel = LeafcutterFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Queen:
                        shipLabel = QueenFleetLabel;
                        break;
                    case ConfigData.ShipTypes.Wasp:
                        shipLabel = WaspFleetLabel;
                        break;
                    case ConfigData.ShipTypes.YellowJacket:
                        shipLabel = YellowJacketFleetLabel;
                        break;
                }
                //Debug.Log($"About to access the parent, {shipLabel}");
                //Debug.Log($"About to access the parent, {shipLabel.transform}");
                //Debug.Log($"About to access the parent, {shipLabel.transform.parent}");

                Transform parent = shipLabel.transform.parent;
                List<FleetShip> availableShips = ConfigData.CurrentShips.GetAvailableShipsOfType(type);
                List<FleetShip> visibleShips = ConfigData.CurrentShips.GetAliveShipsOfType(type);

                // if ship type has any visible ships
                if (visibleShips.Any() && ConfigData.UserProgressData.VisibleShipTypes.Contains(type))
                {
                    //Debug.Log($"Setting the ship count for {type}");
                    // get the count of the ship type and update the label
                    TMP_Text labelText = shipLabel.GetComponentInChildren<TMP_Text>();
                    labelText.text = $"({availableShips.Count})";

                    if (parent != null)
                    {
                        parent.gameObject.SetActive(true);
                    }
                    if (type == ConfigData.ShipTypes.Scout)
                    {
                        BeaconFleetLabel.transform.parent.gameObject.SetActive(true);
                    }
                    else if (type == ConfigData.ShipTypes.Carrier)
                    {
                        StrikerFleetLabel.transform.parent.gameObject.SetActive(true);
                        DroneFleetLabel.transform.parent.gameObject.SetActive(true);
                    }
                }
                else // if not, set the label to inactive
                {
                    //Debug.Log($"There were no visible ships for {type}");
                    parent.gameObject.SetActive(false);
                }
            });
        }
        private void SetupSavedSquadsList()
        {
            //Debug.Log("Setting up the list of saved squads");
            ConfigData.CurrentShips.GetSavedSquads().Where((s) => s.Side == Side).ToList().ForEach((savedSquad) =>
            {
                AddSavedSquadToList(savedSquad);
            });
        }
        private void LoadScene()
        {
            //Debug.Log("Loading scene!");
            SceneManager.LoadSceneAsync(_nextScene, LoadSceneMode.Single);
        }



        // Dialogues
        public void ConfirmDeleteSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (_currentSquad != null)
            {
                if (_currentSquad.HasBeenSavedToStorage)
                {
                    DeleteSquadConfirmation.Show();
                }
                else
                {
                    ClearSquadConfirmation.Show();
                }
            }
        }
        public void ConfirmClearSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (_currentSquad != null && _currentSquad.HasChanged)
            {
                ClearSquadConfirmation.Show();
            }
            else
            {
                if (_currentSquad != null)
                {
                    ClearChanges();
                }
            }
        }
        public void WaitForDoubleClick(GameObject label)
        {
            if (_singleClick) // already has clicked
            {
                //Debug.Log("Already clicked once, marking double click, loading squad");
                _doubleClick = true;
                ConfirmLoadSquad();
            }
            else // first click
            {
                //Debug.Log("First click");
                //Debug.Log(TimeScale);
                _singleClick = true;
                int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                SavedSquad squad = ConfigData.CurrentShips.GetSavedSquads().Where((s) => s.Id == id).First();
                _squadToLoad = squad;
                _squadToChoose = squad;
                Invoke(nameof(ResetSingleClick), .5f);
            }

        }
        public void ResetSingleClick()
        {
            if (!_doubleClick) // has not double clicked and opened the load squad label
            {
                //Debug.Log("No double click, loading squad");
                ConfirmChooseSquad();

            }
            //Debug.Log("Resetting click");
            _singleClick = false;
            _doubleClick = false;

        }
        public void ConfirmLoadSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (_currentSquad == null || !_currentSquad.HasChanged)
            {
                LoadSquad();
            }
            else
            {
                LoadSquadConfirmation.Show();
            }
        }
        public void ConfirmChooseSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            //int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
            //SavedSquad squad = ConfigData.AllShips.GetSavedSquads().Where((s) => s.Id == id).First();
            //_squadToChoose = squad;
            if (!_squadToChoose.HasDeadShips)
            {
                ChooseSquad();
            }
            else if (_squadToChoose.HasAliveShips)
            {
                ChooseSquadConfirmation.Show();
            }
            else
            {
                ChoosingDeadSquadAlert.Show();
            }
        }
        public void ConfirmUnchooseSquad(GameObject label)
        {
            UIAudioController.Instance.PlayButtonSound();
            int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
            SavedSquad squad = ConfigData.CurrentShips.GetSavedSquads().Where((s) => s.Id == id).First();
            _squadToUnchoose = squad;


            // Change these lines in order to show the confirmation dialogue and then do the action, or to just do the action
            UnchooseSquad();
            //UnchooseSquadConfirmation.Show();
        }
        public void ConfirmStartLevel()
        {
            UIAudioController.Instance.PlayButtonSound();
            //Debug.Log("Starting level!");
            int capacity = _capacity;
            if (SupplyUsedInChosenSquads() > capacity)
            {
                OverCapacityAlert.Show();
            }
            else if (_chosenSquads.Count == 0)
            {
                NoChosenSquadsAlert.Show();

            }
            else
            {
                StartLevel();
            }
        }
        public void ConfirmGoBack()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (_currentSquad == null || !_currentSquad.HasChanged)
            {
                GoBack();
            }
            else
            {
                GoBackConfirmation.Show();
            }
        }


        // UI Methods
        private GameObject GetShipIconContainer(ConfigData.ShipTypes shipType)
        {
            switch (shipType)
            {
                case ConfigData.ShipTypes.Barge:
                    return BargeShipIcon;
                case ConfigData.ShipTypes.Beacon:
                    return BeaconShipIcon;
                case ConfigData.ShipTypes.Carrier:
                    return CarrierShipIcon;
                case ConfigData.ShipTypes.Cruiser:
                    return CruiserShipIcon;
                case ConfigData.ShipTypes.Dreadnought:
                    return DreadnoughtShipIcon;
                case ConfigData.ShipTypes.Factory:
                    return FactoryShipIcon;
                case ConfigData.ShipTypes.FireBarge:
                    return FireBargeShipIcon;
                case ConfigData.ShipTypes.Flagship:
                    return FlagshipShipIcon;
                case ConfigData.ShipTypes.Frigate:
                    return FrigateShipIcon;
                case ConfigData.ShipTypes.Gunship:
                    return GunshipShipIcon;
                case ConfigData.ShipTypes.Scout:
                    return ScoutShipIcon;
                case ConfigData.ShipTypes.WarpGate:
                    return WarpGateShipIcon;
                case ConfigData.ShipTypes.Drone:
                    return DroneShipIcon;
                case ConfigData.ShipTypes.Striker:
                    return StrikerShipIcon;

                case ConfigData.ShipTypes.Beehive:
                    return BeehiveShipIcon;
                case ConfigData.ShipTypes.Bumblebee:
                    return BumblebeeShipIcon;
                case ConfigData.ShipTypes.CarpenterBee:
                    return CarpenterBeeShipIcon;
                case ConfigData.ShipTypes.Honeybee:
                    return HoneybeeShipIcon;
                case ConfigData.ShipTypes.Hornet:
                    return HornetShipIcon;
                case ConfigData.ShipTypes.Leafcutter:
                    return LeafcutterShipIcon;
                case ConfigData.ShipTypes.Queen:
                    return QueenShipIcon;
                case ConfigData.ShipTypes.Wasp:
                    return WaspShipIcon;
                case ConfigData.ShipTypes.YellowJacket:
                    return YellowJacketShipIcon;
            }
            return null;
        }
        private void AddSavedSquadToList(SavedSquad savedSquad)
        {
            // instantiate a squad label
            SavedSquadPrefab.SetActive(true);
            GameObject squadLabel = Instantiate(SavedSquadPrefab);
            squadLabel.name = $"Saved Squad - {savedSquad.Name} #{savedSquad.Id}";
            ConfigData.ShipTypes shipType = savedSquad.GetMostValuableShip().GetFleetShip().Type;


            GameObject nameLabel = squadLabel.transform.Find("Squad Name").gameObject;
            GameObject squadIconContainer = Instantiate(GetShipIconContainer(shipType));
            squadIconContainer.transform.SetParent(squadLabel.transform);
            squadIconContainer.transform.SetAsFirstSibling();
            squadIconContainer.name = "Icon Container";

            // fill in the squad name 
            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            nameLabelText.text = savedSquad.Name;


            // change the color of the icon
            if (savedSquad.HasCustomColor)
            {
                //Debug.Log($"Setting changable pixels for {savedSquad.Name}");
                UnityEngine.UI.Image squadIconImage = squadIconContainer.transform.GetChild(0).GetComponent<UnityEngine.UI.Image>();

                int[] changeablePixels = Utilities.GetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
                //Debugger.PrintList(changeablePixels.ToList());
                squadIconImage.sprite = Utilities.SetImageColor(savedSquad.Color, squadIconImage.sprite, changeablePixels);
            }

            // assign it to the squad list
            squadLabel.transform.SetParent(SavedSquadList.transform);


            squadLabel.transform.localScale = new Vector3(1, 1, 1);
            squadLabel.transform.localPosition = new Vector3(squadLabel.transform.position.x, squadLabel.transform.position.y, 1);
            SavedSquadPrefab.SetActive(false);
        }
        private void AddChosenSquadToList(SavedSquad chosenSquad)
        {
            // instantiate a squad label
            ChosenSquadPrefab.SetActive(true);
            GameObject squadLabel = Instantiate(ChosenSquadPrefab);
            squadLabel.name = $"Chosen Squad - {chosenSquad.Name} #{chosenSquad.Id}";
            ConfigData.ShipTypes shipType = chosenSquad.GetMostValuableShip().GetFleetShip().Type;


            GameObject nameLabel = squadLabel.transform.Find("Squad Name").gameObject;
            GameObject squadIconContainer = Instantiate(GetShipIconContainer(shipType));
            squadIconContainer.transform.SetParent(squadLabel.transform);
            squadIconContainer.transform.SetAsFirstSibling();
            squadIconContainer.name = "Icon Container";

            // fill in the squad name 
            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            nameLabelText.text = chosenSquad.Name;


            // change the color of the icon
            if (chosenSquad.HasCustomColor)
            {
                //Debug.Log($"Setting changable pixels for {savedSquad.Name}");
                UnityEngine.UI.Image squadIconImage = squadIconContainer.transform.GetChild(0).GetComponent<UnityEngine.UI.Image>();

                int[] changeablePixels = Utilities.GetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
                //Debugger.PrintList(changeablePixels.ToList());
                squadIconImage.sprite = Utilities.SetImageColor(chosenSquad.Color, squadIconImage.sprite, changeablePixels);
            }

            // assign it to the squad list
            squadLabel.transform.SetParent(ChosenSquadList.transform);


            squadLabel.transform.localScale = new Vector3(1, 1, 1);
            squadLabel.transform.localPosition = new Vector3(squadLabel.transform.position.x, squadLabel.transform.position.y, 1);
            ChosenSquadPrefab.SetActive(false);
        }
        private void UpdateSavedSquadInList(GameObject instance, SavedSquad savedSquad)
        {
            GameObject nameLabel = instance.transform.Find("Squad Name").gameObject;
            GameObject iconContainer = instance.transform.Find("Icon Container").gameObject;
            Transform squadLabel = iconContainer.transform.parent;

            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            ConfigData.ShipTypes shipType = savedSquad.GetMostValuableShip().GetFleetShip().Type;

            //Destroy old icon container
            Destroy(iconContainer);


            // Make new icon container
            GameObject squadIconContainer = Instantiate(GetShipIconContainer(shipType));
            squadIconContainer.transform.SetParent(squadLabel);
            squadIconContainer.transform.SetAsFirstSibling();
            squadIconContainer.name = "Icon Container";
            squadIconContainer.transform.localScale = new Vector3(1, 1, 1);

            // fill in the squad name and icon
            UnityEngine.UI.Image squadIconImage = squadIconContainer.transform.GetChild(0).GetComponent<UnityEngine.UI.Image>();

            nameLabelText.text = savedSquad.Name;
            nameLabel.transform.parent.name = $"Saved Squad - {savedSquad.Name} #{savedSquad.Id}";
            squadIconImage.sprite = _spriteTypes.GetValueOrDefault(shipType);
            //squadIconImage.SetNativeSize();
            //squadIconImage.transform.localScale = new Vector3(.1f, .1f, 0);

            // change the color of the icon
            if (savedSquad.HasCustomColor)
            {
                int[] changeablePixels = Utilities.GetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
                squadIconImage.sprite = Utilities.SetImageColor(savedSquad.Color, squadIconImage.sprite, changeablePixels);
            }

            if (savedSquad.HasDeadShips)
            {
                //Debug.Log($"{squad.Name} still has dead ships");
                //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}"));
                //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>());
                //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color);
                GameObject.Find($"Saved Squad - {savedSquad.Name} #{savedSquad.Id}").GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
            }
            else
            {
                GameObject.Find($"Saved Squad - {savedSquad.Name} #{savedSquad.Id}").GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("saved-squad-label-default-color");
            }

        }
        private void RemoveSavedSquadFromList(SavedSquad savedSquad)
        {
            Destroy(GameObject.Find($"Saved Squad - {savedSquad.Name} #{savedSquad.Id}"));
        }
        private void RemoveChosenSquadFromList(SavedSquad squad)
        {
            Destroy(GameObject.Find($"Chosen Squad - {squad.Name} #{squad.Id}"));
        }
        private void UpdateSquadMakerSupplyLabel()
        {
            int supply = SupplyUsedInSquadMaker();
            TMP_Text text = SquadMakerSupplyCapacityLabel.GetComponentInChildren<TMP_Text>();
            text.text = $"Supply Capacity: {supply.ToString("N0")}";
        }
        private void UpdateChosenSquadsSupplyLabel()
        {
            int supply = SupplyUsedInChosenSquads();
            //Debug.Log($"Supply capacity {ConfigData.StartingSettings.SupplyCapacity.Count}, {Side}");
            GameObject container = ChosenSquadsSupplyCapacityLabel.transform.parent.gameObject;
            TMP_Text text = ChosenSquadsSupplyCapacityLabel.GetComponentInChildren<TMP_Text>();


            text.text = $"Supply Capacity: {supply.ToString("N0")} / {_capacity.ToString("N0")}";
            if (supply > _capacity)
            {
                container.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
            }
            else
            {
                container.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("supply-capacity-label");
            }
        }
        // [alert] this should be rewritten to be more performant
        public void UpdateShipCounter(FleetShip fleetShip)
        {
            GameObject inventoryContainer = FindObjectsByType<GameObject>(FindObjectsSortMode.None).ToList().Find((gameObject) => gameObject.name == $"{Utilities.ConvertShipTypeToName[fleetShip.Type]} Inventory Ship");
            //Debug.Log($"{inventoryContainer}, {$"{Utilities.ConvertShipTypeToName[fleetShip.Type]} Inventory Ship"}");
            if (inventoryContainer != null)
            {
                GameObject shipCountLabel = inventoryContainer.transform.GetChild(2).gameObject;
                TMP_Text text = shipCountLabel.GetComponent<TMP_Text>();
                if (text != null)
                {
                    text.text = $"({_fleetList.Where((ship) => ship.Type == fleetShip.Type).ToList().Count})";
                    inventoryContainer.SetActive(true);
                }
            }
        }
        private void UpdateSquadShipCounter()
        {
            TMP_Text shipCountLabel = SquadShipCountLabel.GetComponent<TMP_Text>();
            shipCountLabel.text = $"{(_currentSquad != null ? _currentSquad.GetSquadShips().Count : 0)} / {ConfigData.Configuration.MaxSquadSize}";
        }
        public void UpdateSquadUI()
        {
            UpdateSquadShipCounter();
            UpdateSquadMakerSupplyLabel();
        }
        public void ToggleLevelOptions(bool show)
        {
            FogOfWarLabel.SetActive(show);
            FogOfWarDropdown.SetActive(show);
            MiningLabel.SetActive(show);
            MiningDropdown.SetActive(show);
            EnemyReinforcementsLabel.SetActive(show);
            EnemyReinforcementsDropdown.SetActive(show);
            MapLabel.SetActive(show);
            MapDropdown.SetActive(show);
            AsteroidsLabel.SetActive(show);
            AsteroidsDropdown.SetActive(show);
            ObstaclesLabel.SetActive(show);
            ObstaclesDropdown.SetActive(show);
            OpposingForceLabel.SetActive(show);
            OpposingForcePresetDropdown.SetActive(show);
            ChosenEnemyShipTypeLabel.SetActive(show);
            ChosenEnemyShipTypesDropdown.SetActive(show);

            //Debug.Log($"Changing height to {(show ? _squadListOptionsScrollHeight : _squadListOriginalScrollHeight)} because show is {show}");
            RectTransform squadListRect = ChosenSquadList.transform.parent.parent.GetComponent<RectTransform>();
            squadListRect.sizeDelta = new Vector2(squadListRect.sizeDelta.x, (show ? _squadListOptionsScrollHeight : _squadListOriginalScrollHeight));
        }
        public void ToggleLevelDetails(bool show)
        {
            //Debug.Log($"Changing height to {(show ? _squadListLevelScrollHeight : _squadListOriginalScrollHeight)} because show is {show}");
            LevelTitleContainer.SetActive(show);
            LevelDetailsContainer.SetActive(show);
            RectTransform squadListRect = ChosenSquadList.transform.parent.parent.GetComponent<RectTransform>();
            squadListRect.sizeDelta = new Vector2(squadListRect.sizeDelta.x, (show ? _squadListLevelScrollHeight : _squadListOriginalScrollHeight));
        }


        // Utility methods
        private int SupplyUsedInSquadMaker()
        {
            int sum = 0;
            if (_currentSquad != null)
            {
                sum += _currentSquad.GetCapacity();
            }
            return sum;
        }
        private int SupplyUsedInChosenSquads()
        {
            int sum = 0;
            if (_chosenSquads != null)
            {

                sum += _chosenSquads.Sum((s) => s.GetCapacity());
            }
            return sum;
        }

        // Squad management
        public SavedSquad GetCurrentSquad()
        {
            return _currentSquad;
        }
        public List<FleetShip> GetFleetList()
        {
            return _fleetList;
        }
        public string GetSquadName()
        {
            return _nameText;
        }
        public Color GetSquadColor()
        {
            return _squadColor;
        }
        public void SetCurentSquad(SavedSquad squad)
        {
            _currentSquad = squad;
        }
        private void SetSquadColor(Color color)
        {
            _squadColor = color;
            if (_currentSquad != null)
            {
                _currentSquad.Color = color;
                _currentSquad.HasCustomColor = true;
                GetDropper().GetDragIcons().ForEach((icon) =>
                {
                    //icon.GetIcon().GetComponent<Image>().color = color;
                    icon.SetColor(color);
                });
                _currentSquad.SetChanged(true);
            }
        }
        public void ClearUnsavedSquad()
        {
            SavedSquad savedSquad = ConfigData.CurrentShips.GetSavedSquad(_currentSquad.Id);
            _currentSquad.Id = -1; // make sure it doesn't match any existing squad

            // remove all icons from the screen
            GetDropper().RemoveDragIcons();

            // clear name, drag icon, and color
            _nameText = "";
            _squadColor = ConfigData.UnsetColor;
            SquadNameInput.GetComponent<TMP_InputField>().text = "";

            if (savedSquad != null)
            {
                List<ConfigData.ShipTypes> updatedShipTypes = new List<ConfigData.ShipTypes>();
                savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    ConfigData.ShipTypes shipType = ship.ShipType;
                    if (!updatedShipTypes.Contains(shipType))
                    {
                        updatedShipTypes.Add(shipType);
                        UpdateShipCounter(ship.GetFleetShip());
                    }
                });
            }

            // remove the unsaved squad
            _currentSquad = null;


            // close the color picker
            CloseColorPicker();
            UpdateSquadUI();
            SquadActionBox.SetActive(false);
        }
        public void SaveSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (HasCurrentSquad)
            {
                _currentSquad.OrientSquad();
                //Debug.Log($"Saving {_currentUnsavedSquad.Name}");

                //Debug.Log($"Squad starting position: {_currentUnsavedSquad.StartingPosition}");

                if (ConfigData.CurrentShips.DoesSquadExist(_currentSquad.Id))
                {
                    SaveExistingSquad();
                }
                else
                {
                    SaveNewSquad();
                }

                //Debug.Log($"Added _currentUnsavedSquad to SavedSquad list");
                //Debug.Log($"_currentUnsavedSquad: {_currentUnsavedSquad.GetShips().Count}, SavedSquad entry: {_savedSquadsData.GetSquads().Last().GetShips().Count}");


                //Debug.Log($"Made _currentUnsavedSquad null");
                //Debug.Log($"_currentUnsavedSquad: {_currentUnsavedSquad}");
                //Debug.Log($"SavedSquad entry: {_savedSquadsData.GetSquads().Last().GetShips().Count}");

                //Debug.Log($"JSON : {_currentUnsavedSquad.ToJson()}");
                //ConfigData.WriteJsonFile(_currentUnsavedSquad.ToJson());
            }
        }
        public void ClearChanges()
        {
            //Debug.Log("Clearing changes");
            SavedSquad savedSquad = ConfigData.CurrentShips.GetSavedSquad(_currentSquad.Id);
            if (savedSquad != null)
            {
                _fleetList.RemoveAll((fleetShip) => savedSquad.HasShip(fleetShip));
            }
            if (HasCurrentSquad)
            {
                ClearUnsavedSquad();

            }
        }
        public void DeleteCurrentSquad()
        {
            if (HasCurrentSquad)
            {
                // add all the ships back into the fleet list
                //_currentUnsavedSquad.GetShips().ForEach((ship) =>
                //{
                //    FleetShip fleetShip = ship.GetFleetShip();
                //    _fleetList.Add(fleetShip);
                //});

                // remove the squad from the saved squad list
                ConfigData.CurrentShips.RemoveSquad(_currentSquad);

                // remove the entry from the squad ui list
                RemoveSavedSquadFromList(_currentSquad);

                // clear the squad maker
                ClearUnsavedSquad();

                // save the squads
                ConfigData.CurrentShips.SaveSquadData();
            }
        }
        public void DuplicateCurrentSquad()
        {
            UIAudioController.Instance.PlayButtonSound();
            if (HasCurrentSquad)
            {
                SavedSquad originalSquad = _currentSquad;
                _currentSquad = (SavedSquad)originalSquad.Clone();
                List<SquadShip> originalSquadShips = _currentSquad.GetSquadShips().ToList();
                _currentSquad.GetSquadShips().Clear();
                originalSquadShips.ForEach((originalSquadShip) =>
                {
                    FleetShip fleetShip = GetFleetList().Where((s) => s.Type == originalSquadShip.ShipType).FirstOrDefault();
                    if (fleetShip != null)
                    {
                        //Debug.Log($"Adding {fleetShip} to duplicated squad {_currentSquad.Name}");
                        _currentSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, originalSquadShip.Offset - originalSquad.StartingPosition));
                        GetFleetList().Remove(fleetShip);
                    }
                    //else
                    //{
                    //    Debug.Log($"Could not add fleetShip to duplicated squad {_currentSquad.Name}");
                    //}
                });


                if (_currentSquad.GetSquadShips().Count == originalSquadShips.Count)
                {
                    _currentSquad.Stats = new SquadStatBlock(Utilities.GenerateCommanderName(), 0, 0, 0, 0, 0, 0);
                    _currentSquad.StartingPosition = originalSquad.StartingPosition;
                    SaveNewSquad();
                    _currentSquad = null;
                }
                else
                {
                    CannotDuplicateSquad.Show();
                }

            }
        }
        public void SaveNewSquad()
        {
            //Debug.Log("New squad, does not exist yet");
            _currentSquad.Id = ConfigData.UserProgressData.GetNextSavedSquadId();
            if (_currentSquad.Name == "")
            {
                _currentSquad.Name = $"Squadron #{ConfigData.UserProgressData.GetNextSavedSquadNumber()}";
            }
            ConfigData.CurrentShips.AddSquad(_currentSquad);
            AddSavedSquadToList(ConfigData.CurrentShips.GetSavedSquads().Last());

            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();
            SquadSavingStatus.Show();
            StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipPartSprites, "ship", ConfigData.ShipSizes, SquadSavingStatus));
            StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipRemainsSprites, "remains", ConfigData.ShipRemainsSizes));
            ClearUnsavedSquad();


        }
        public void SaveExistingSquad()
        {
            //Debug.Log($"Squad does exist, replacing old squad with {_currentSquad.Name}");
            SavedSquad oldSavedSquad = ConfigData.CurrentShips.GetSavedSquad(_currentSquad.Id);

            UpdateSavedSquadInList(GameObject.Find($"Saved Squad - {oldSavedSquad.Name} #{oldSavedSquad.Id}"), _currentSquad);
            List<SavedSquad> savedSquads = ConfigData.CurrentShips.GetSavedSquads();
            int replacementIndex = savedSquads.IndexOf(oldSavedSquad);
            savedSquads[replacementIndex] = (SavedSquad)_currentSquad.Clone();

            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();
            if (oldSavedSquad.Color != _currentSquad.Color || oldSavedSquad.GetSquadShips().Count != _currentSquad.GetSquadShips().Count ||
                oldSavedSquad.GetSquadShips().Any((s) => _currentSquad.GetShip(s.GetFleetShip().Id) == null))
            {
                SquadSavingStatus.Show();
                StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipPartSprites, "ship", ConfigData.ShipSizes, SquadSavingStatus));
                StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipRemainsSprites, "remains", ConfigData.ShipRemainsSizes));
            }
            ClearUnsavedSquad();
        }
        public void LoadSquad()
        {
            if (HasCurrentSquad)
            {
                ClearUnsavedSquad();
            }
            //SavedSquad squad = _squadToLoad;
            SquadStatBlock stats = _squadToLoad.Stats;
            //Debug.Log($"Loading squad {squad.Name}");

            // set the name text, color and current squad
            _nameText = _squadToLoad.Name;
            _squadColor = _squadToLoad.Color;
            _currentSquad = (SavedSquad)_squadToLoad.Clone();
            SquadNameInput.GetComponent<TMP_InputField>().text = _nameText;

            // close the color picker if active
            CloseColorPicker();
            // make and position all the drag icons
            _currentSquad.GetSquadShips().ForEach((ship) =>
            {
                ship.SetOffset(_currentSquad.StartingPosition + ship.Offset);
                //Debug.Log($"Set offset for {ship.GetFleetShip().Name}: {ship.Offset}");

            });
            Dropper dropper = GetDropper();
            _currentSquad.GetSquadShips().ForEach((ship) =>
            {
                //Vector2 placementPosition = Utilities.WorldUnitsToScreenPixels(new Vector2(squad.StartingPosition.x + ship.Offset.x, squad.StartingPosition.y + ship.Offset.y), Camera);
                Vector2 placementPosition = Camera.WorldToScreenPoint(ship.Offset);
                //Vector2 placementPosition = Camera.WorldToScreenPoint(ship.Offset);

                //ship.SetOffset(offsetPosition);
                Debug.Log($"Starting Position for {ship.GetFleetShip().Name}: {_currentSquad.StartingPosition}, Offset position: {ship.Offset}, Placement position: {placementPosition}");
                dropper.MakeDragIcon(ship.GetFleetShip());
                dropper.SetupActiveDragging(placementPosition, true);
                DragIcon dragIcon = dropper.GetCurrentDragIcon();
                dragIcon.SetColor(_currentSquad.Color);
                dragIcon.Reposition(placementPosition, ship);
            });
            if (HasActionBox)
            {
                ActionBox.SetupForSquad();
            }
            //set unchanged
            _currentSquad.SetChanged(false);
            //_squadToLoad = null;


        }
        public void ChooseSquad()
        {
            //ClearDialogues();
            if (!_chosenSquads.Contains(_squadToChoose) && (!HasCurrentSquad || !_currentSquad.Equals(_squadToChoose) || !_currentSquad.HasChanged))
            {
                if (HasCurrentSquad && _currentSquad.Equals(_squadToChoose) && !_currentSquad.HasChanged)
                {
                    ClearUnsavedSquad();
                }
                _chosenSquads.Add(_squadToChoose);
                AddChosenSquadToList(_squadToChoose);
                RemoveSavedSquadFromList(_squadToChoose);
                UpdateChosenSquadsSupplyLabel();
            }
            else if (HasCurrentSquad && _currentSquad.Equals(_squadToChoose) && _currentSquad.HasChanged)
            {
                ChoosingUnsavedSquadAlert.Show();
            }
            _squadToChoose = null;
        }
        public void UnchooseSquad()
        {
            _chosenSquads.Remove(_squadToUnchoose);
            RemoveChosenSquadFromList(_squadToUnchoose);
            AddSavedSquadToList(_squadToUnchoose);
            UpdateChosenSquadsSupplyLabel();
            _squadToUnchoose = null;
        }


        // Drag icons and placement
        public GameObject GetDragIconPrefab(ConfigData.ShipTypes type)
        {
            return _dragIconTypes.GetValueOrDefault(type);
        }
        public void AutoPlaceShip(string ship)
        {
            UIAudioController.Instance.PlayButtonSound();
            _dropper.AutoPlaceShip(Utilities.ConvertShipNameToShipType[ship]);
        }
        public Dropper GetDropper()
        {
            return _dropper;
        }
        public void SetFormation(string formation)
        {
            UIAudioController.Instance.PlayButtonSound();
            Dropper dropper = GetDropper();
            if (HasCurrentSquad && dropper.GetDragIcons().Count > 0)
            {
                _currentSquad.GetSquadShips().Clear();
                CurrentFormation = formation;
                switch (formation)
                {
                    case "Line":
                        dropper.LineFormation();
                        break;
                    case "Box":
                        dropper.BoxFormation();
                        break;
                    case "Arrow":
                        dropper.PyramidFormation(true);
                        break;
                    case "Rectangle":
                        dropper.RectangleFormation();
                        break;
                    case "Pyramid":
                        dropper.PyramidFormation(false);
                        break;
                }
            }
        }
        public void FleetDragStart(string ship)
        {
            ConfigData.ShipTypes shipType = Utilities.ConvertShipNameToShipType[ship];
            Dropper dropper = GetDropper();
            dropper.PullNewDragIcon(shipType);
            dropper.SetupActiveDragging(Input.mousePosition, false);

        }
        public void FleetDragStart(GameObject label)
        {
            if (label != null)
            {
                int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                FleetShip ship = ConfigData.CurrentShips.GetFleetShip(id);

                Dropper dropper = GetDropper();
                dropper.StartDragExistingIcon(ship);
                if (HasCurrentSquad)
                {
                    SquadShip squadShip = _currentSquad.GetShip(ship.Id);
                    _currentSquad.RemoveShipFromSquad(squadShip);
                    dropper.SetupActiveDragging(Input.mousePosition, false);
                }
            }

        }
        public void FleetDragging()
        {
            //Debug.Log($"Dragging {_currentDragIcon.Icon.name}");
            GetDropper().DraggingNewIcon();
        }
        public void FleetDragEnd()
        {
            GetDropper().EndDragging();
        }


        // UI Interaction
        public void ShowBuildButtonMessage()
        {
            BuildButtonHighlight.SetActive(true);
            BuildButtonMessage.SetActive(true);
        }
        public void HideBuildButtonMessage()
        {
            BuildButtonHighlight.SetActive(false);
            BuildButtonMessage.SetActive(false);
            ConfigData.UserProgressData.HasSeenBuildInterface = true;
            ConfigData.UserProgressData.Save();
        }
        public void SetupBuildInterface()
        {
            List <ConfigData.ShipTypes> list = ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide ? ConfigData.UserProgressData.VisibleHumanShipTypes.ToList() : ConfigData.UserProgressData.VisibleBeeShipTypes.ToList();

            List<ConfigData.ShipTypes> hideList = ShipsBeingBuilt.Keys.ToHashSet().Except(list).ToList();
            BuildPopup.SetActive(true);
            list.ToList().ForEach((shipType) =>
            {
                GameObject shipLabel = GameObject.Find($"{Utilities.ConvertShipTypeToName[shipType]} Build Ship");
                shipLabel.transform.Find("Ship Cost").GetComponent<TMP_Text>().text = $"Cost: {ConfigData.GetShipInfo(shipType).Tsv}";
            });
            hideList.ToList().ForEach((shipType) =>
            {
                GameObject.Find($"{Utilities.ConvertShipTypeToName[shipType]} Build Ship").SetActive(false);
            });
            BuildPopup.SetActive(false);
        }
        public void ShowBuildInterface()
        {
            BuildPopup.SetActive(true);
        }
        public void BuildShips()
        {
            if (TotalBuildCost <= ConfigData.UserProgressData.MinedTSV)
            {
                ConfigData.UserProgressData.MinedTSV -= TotalBuildCost;
                TotalBuildCost = 0;
                MineralsText.text = $"Minerals: {ConfigData.UserProgressData.MinedTSV.ToString("N0")}";
                TotalBuildCostText.text = $"Total Cost: {TotalBuildCost.ToString("N0")}";

                ShipsBeingBuilt.Keys.ToList().ForEach(key => {
                    int shipCount = ShipsBeingBuilt[key];

                    if (shipCount > 0)
                    {
                        ConfigData.CurrentShips.AddShipsToFleet(key, shipCount);

                        GameObject.Find($"{Utilities.ConvertShipTypeToName[key]} Build Ship").transform.Find("Ship Count").GetComponent<TMP_Text>().text = "(0)";

                        GameObject.Find($"{Utilities.ConvertShipTypeToName[key]} Inventory Ship").transform.Find("Ship Count").GetComponent<TMP_Text>().text = $"({ConfigData.CurrentShips.GetAvailableShipsOfType(key).Count})";

                        ShipsBeingBuilt[key] = 0;
                    }



                });
                BuildPopup.SetActive(false);

                ConfigData.UserProgressData.Save();
                ConfigData.CurrentShips.SaveFleetData();

            }
        }
        public void AddShip(string shipString)
        {
            ConfigData.ShipTypes ship = Utilities.ConvertShipNameToShipType[shipString];
            ShipsBeingBuilt[ship]++;
            GameObject.Find($"{shipString} Build Ship").transform.Find("Ship Count").GetComponent<TMP_Text>().text = $"({ShipsBeingBuilt[ship]})";
            TotalBuildCost += ConfigData.GetShipInfo(ship).Tsv;
            TotalBuildCostText.text = $"Total Cost: {TotalBuildCost.ToString("N0")}";
        }
        public void SubtractShip(string shipString)
        {
            ConfigData.ShipTypes ship = Utilities.ConvertShipNameToShipType[shipString];
            if (ShipsBeingBuilt[ship] > 0)
            {
                ShipsBeingBuilt[ship]--;

                GameObject.Find($"{shipString} Build Ship").transform.Find("Ship Count").GetComponent<TMP_Text>().text = $"({ShipsBeingBuilt[ship]})";

                TotalBuildCost -= ConfigData.GetShipInfo(ship).Tsv;

                TotalBuildCostText.text = $"Total Cost: {TotalBuildCost.ToString("N0")}";
            }
        }
        public void ShowShipInfo(string ship)
        {
            if (!GetDropper().IsDragging)
            {
                ConfigData.ShipTypes shipType = Utilities.ConvertShipNameToShipType[ship];
                TMP_Text titleText = ShipInfoBoxTitle.GetComponent<TMP_Text>();
                TMP_Text detaislText = ShipInfoBoxDetails.GetComponent<TMP_Text>();
                ShipStatBlock shipInfo = ConfigData.GetShipInfo(shipType); 

                titleText.text = $"{ship} Details";
                detaislText.text = $"{shipInfo.Description}\n\n" +
                    $"Health: {shipInfo.Health.ToString("N0")}\n" +
                    $"Vision: {shipInfo.PrintVision()}\n" +
                    $"Range: {shipInfo.PrintRange()}\n" +
                    $"Power: {shipInfo.PrintPower()}\n" +
                    $"Rate of Fire: {shipInfo.PrintRateOfFire()}\n" +
                    $"Speed: {shipInfo.Speed}\n" +
                    $"Capacity: {(shipType != ConfigData.ShipTypes.Drone && shipType != ConfigData.ShipTypes.Striker && shipType != ConfigData.ShipTypes.Beacon ? ConfigData.CurrentShips.GetShipsOfType(shipType).First().GetCapacity().ToString("N0") : "N/A")}";

                UnityEngine.UI.Image image = ShipInfoBoxIcon.GetComponent<UnityEngine.UI.Image>();
                image.sprite = _spriteTypes.GetValueOrDefault(shipType);
                image.SetNativeSize();
                if (shipType == ConfigData.ShipTypes.Queen)
                {
                    image.transform.localScale = new Vector3(.01f, .01f, 0);
                }
                else
                {
                    image.transform.localScale = new Vector3(.1f, .1f, 0);
                }

                ShipInfoBox.SetActive(true);
                _showShipInfo = true;
            }
        }
        public void HideShipInfo()
        {
            _showShipInfo = false;
            Invoke(nameof(DelayedHideShipInfo), .5f);


        }
        private void DelayedHideShipInfo()
        {
            if (!_showShipInfo)
            {
                ShipInfoBox.SetActive(false);
            }
        }
        public void ShowSquadInfo(GameObject label)
        {
            if (!GetDropper().IsDragging)
            {

                if (label != null)
                {
                    int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                    SavedSquad squad = ConfigData.CurrentShips.GetSavedSquads().Where((s) => s.Id == id).First();
                    SquadStatBlock stats = squad.Stats;
                    //Debug.Log($"Squad ID: {id}");

                    TMP_Text titleText = SquadInfoBoxTitle.GetComponent<TMP_Text>();
                    TMP_Text detaislText = SquadInfoBoxDetails.GetComponent<TMP_Text>();


                    titleText.text = $"{squad.Name}";
                    detaislText.text = $"Commander: {stats.Commander}\n\n" +
                        $"Ships: {(squad.GetSquadShips().Count - squad.GetDeadShips().Count).ToString("N0")} / {squad.GetSquadShips().Count.ToString("N0")} " +
                        $"{(squad.HasDeadShips ? $" <color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(ConfigData.GetUIColor("bad"))}><smallcaps><b>(Unfilled)</b></smallcaps></color>" : "")}\n" +
                        $"Capacity: {squad.GetCapacity().ToString("N0")} / {squad.GetMaxCapacity().ToString("N0")}\n" +
                        $"Battles: {stats.BattlesFought.ToString("N0")}: {stats.BattlesWon}W - {stats.BattlesLost}L     (#{ConfigData.CurrentShips.GetSquadRanking(squad, "Record")})\n" +
                        $"Damage Done: {stats.DamageDone.ToString("N0")}     (#{ConfigData.CurrentShips.GetSquadRanking(squad, "DamageDone")})\n" +
                        $"Damage Received: {stats.DamageReceived.ToString("N0")}     (#{ConfigData.CurrentShips.GetSquadRanking(squad, "DamageReceived")})\n" +
                        $"Kills: {stats.Kills.ToString("N0")}     (#{ConfigData.CurrentShips.GetSquadRanking(squad, "Kills")})\n" +
                        $"Ships Lost: {stats.ShipsLost.ToString("N0")}     (#{ConfigData.CurrentShips.GetSquadRanking(squad, "ShipsLost")})\n";

                    UnityEngine.UI.Image image = SquadInfoBoxIcon.GetComponent<UnityEngine.UI.Image>();
                    GameObject squadIcon = label.transform.Find("Icon Container/Ship Icon").gameObject;
                    image.sprite = squadIcon.GetComponent<UnityEngine.UI.Image>().sprite;
                    image.SetNativeSize();
                    image.transform.localScale = new Vector3(.1f, .1f, 0);


                    SquadInfoBox.SetActive(true);
                    _showSquadInfo = true;
                }
                else
                {
                    Debug.Log($"No selected object: {label}");
                }

            }
        }
        public void HideSquadInfo()
        {
            _showSquadInfo = false;
            Invoke(nameof(DelayedHideSquadInfo), .5f);


        }
        private void DelayedHideSquadInfo()
        {
            if (!_showSquadInfo)
            {
                SquadInfoBox.SetActive(false);
            }
        }
        public void ShowShipStats(GameObject label)
        {
            if (!GetDropper().IsDragging)
            {

                if (label != null)
                {
                    int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                    _currentShipInfo = ConfigData.CurrentShips.GetFleetShip(id);
                    //Debug.Log($"Squad ID: {id}");
                    //Debug.Log(_currentShipInfo.Name);

                    ShipStatsBoxDetails.GetComponent<TMP_Text>().text = $"Battles: {_currentShipInfo.BattlesFought.ToString("N0")}: {_currentShipInfo.BattlesWon}W - {_currentShipInfo.BattlesLost}L     (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "Record")})\n" +
                        $"Shots Fired: {_currentShipInfo.ShotsFired.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "ShotsFired")})\n" +
                        $"Damage Done: {_currentShipInfo.DamageDone.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "DamageDone")})\n" +
                        $"Damage Received: {_currentShipInfo.DamageReceived.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "DamageReceived")})\n" +
                        $"Kills: {_currentShipInfo.Kills.ToString("N0")}    (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "Kills")})\n" +
                        $"{(_currentShipInfo.Type == ConfigData.ShipTypes.CarpenterBee || _currentShipInfo.Type == ConfigData.ShipTypes.Factory ? $"Minerals Mined: {_currentShipInfo.MineralsMined.ToString("N0")}  (#{ConfigData.CurrentShips.GetShipRanking(_currentShipInfo, "Minerals Mined")})" : "\n")}";


                    ShipStatsBox.SetActive(true);
                    Vector2 mouse = Input.mousePosition;
                    //Vector2 screenPoint = Camera.WorldToScreenPoint(ShipStatsBoxOffset);
                    //Vector2 change = new Vector2(Mathf.Abs(BaseWorldPoint.x - screenPoint.x), Mathf.Abs(BaseWorldPoint.y - screenPoint.y));

                    Vector2 change = Utilities.WorldUnitsToScreenPixels(ShipStatsBoxOffset, Camera);
                    //Vector2 change = ShipStatsBoxOffset;


                    //Debug.Log($"mouse: {mouse}, change: {change}");
                    ShipStatsBox.transform.position = new Vector2(mouse.x + change.x, mouse.y + change.y);

                    TMP_InputField nameInput = ShipNameInput.GetComponent<TMP_InputField>();
                    //EventSystem.current.SetSelectedGameObject(null);
                    //EventSystem.current.SetSelectedGameObject(ShipNameInput);
                    nameInput.ActivateInputField();
                    nameInput.text = _currentShipInfo.Name;
                }
                else
                {
                    Debug.Log($"No selected object: {label}");
                }
                _showShipStatsBox = true;
            }
        }
        public void HideShipStats() {
            _showShipStatsBox = false;
            Invoke(nameof(DelayedHideShipStats), .25f);
        }
        public void DelayedHideShipStats()
        {
            if (!_showShipStatsBox)
            {
                ShipStatsBox.SetActive(false);
            }
        }
        public void ShowTooltip(string tooltip)
        {
            if (!_showTooltip)
            {
                TMP_Text tooltipText = TooltipText.GetComponent<TMP_Text>();
                tooltipText.text = ConfigData.Configuration.Tooltips.GetValueOrDefault(tooltip);
                Tooltip.SetActive(true);
                Vector2 mouse = Input.mousePosition;
                //Vector2 screenPoint = Camera.WorldToScreenPoint(TooltipOffset);
                //Vector2 change = new Vector2(Mathf.Abs(BaseWorldPoint.x - screenPoint.x), Mathf.Abs(BaseWorldPoint.y - screenPoint.y));

                Vector2 change = Utilities.WorldUnitsToScreenPixels(TooltipOffset, Camera);
                //Vector2 change = TooltipOffset;


                //Debug.Log($"mouse: {mouse}, change: {change}");
                Tooltip.transform.position = new Vector2(mouse.x + change.x, mouse.y + change.y);
                _showTooltip = true;
            }

        }
        public void HideTooltip()
        {
            _showTooltip = false;
            Invoke(nameof(DelayedHideTooltip), .25f);

        }
        private void DelayedHideTooltip()
        {
            if (!_showTooltip)
            {
                Tooltip.SetActive(false);
            }
        }
        public void ChangeSquadName(string name)
        {
            name = Utilities.ValidateInputString(name);
            if (_currentSquad != null)
            {
                _currentSquad.Name = name;
                _currentSquad.SetChanged(true);
            }
            _nameText = name;
            SquadNameInput.GetComponent<TMP_InputField>().text = name;
        }
        public void ChangeShipName(string name)
        {
            name = Utilities.ValidateInputString(name);
            if (_currentShipInfo != null)
            {
                _currentShipInfo.Name = name;
            }
            //Debug.Log($"Ship name changed to {name}");
            ShipNameInput.GetComponent<TMP_InputField>().text = name;
        }
        public void OpenColorPicker()
        {
            UIAudioController.Instance.PlayButtonSound();
            //Debug.Log("Opening/Closing color picker");
            _colorPicker.Toggle();
        }
        public void CloseColorPicker()
        {
            if (HasColorPicker && _colorPicker.IsActive)
            {
                _colorPicker.Toggle();
            }
        }
        public void PickColor(BaseEventData data)
        {
            //Debug.Log("Trying to pick color");
            SetSquadColor(_colorPicker.GetColor(data));
        }
        public void SetColor(string color)
        {
            SetSquadColor(_colorPicker.ChangeHexValue(color));
        }
        public void GoBack()
        {
            if (ConfigData.SquadMakerSide == ConfigData.Configuration.SquadMakerFirstSide)
            {
                SceneManager.LoadSceneAsync("Main Menu", LoadSceneMode.Single);

            }
            else if (ConfigData.SquadMakerSide == ConfigData.Configuration.SquadMakerSecondSide)
            {
                ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
                _nextScene = "Squad Maker";
                Invoke(nameof(LoadScene), .25f);
                return;
            }
        }
        private void StartLevel()
        {
            if (_startingLevel)
            {
                Debug.Log("Already starting the level!");
                return;
            }
            _startingLevel = true;
            Invoke(nameof(ProcessStartingLevel), .1f);



        }
        private void ProcessStartingLevel()
        {
            //Debug.Log("On to the level!");

            // reset options
            ConfigData.BeeShipTypes = ConfigData.UserProgressData.VisibleBeeShipTypes;
            ConfigData.HumanShipTypes = ConfigData.UserProgressData.VisibleHumanShipTypes;
            //ConfigData.SelectedObstacleMapIndex = -1;
            //ConfigData.SelectedAsteroidOption = -1;
            //ConfigData.SelectedLevelMapIndex = -1;
            //ConfigData.SelecteFogOfWarOption = -1;
            //ConfigData.SelectedMiningOption = -1;
            //ConfigData.SelectedShipsLoadingMidLevelOption = -1;
            //ConfigData.SelectedEnemyShipTypes = -1;

            //Debug.Log($"SMS: {ConfigData.SquadMakerSide}, SMFS: {ConfigData.Configuration.SquadMakerFirstSide}, SMSS: {ConfigData.Configuration.SquadMakerSecondSide}");
            // go to next side if you need to
            if (!IsRandomizedOpposingSide)
            {


                if (Side == ConfigData.Configuration.SquadMakerFirstSide)
                {
                    //ConfigData.SelectedObstacleMapIndex = _chosenObstacleOption;
                    //ConfigData.SelectedAsteroidOption = _chosenAsteroidsOption;
                    //ConfigData.SelectedLevelMapIndex = _chosenMapOption;
                    //ConfigData.SelecteFogOfWarOption = _chosenFogOfWarOption;
                    //ConfigData.SelectedMiningOption = _chosenMiningOption;
                    //ConfigData.SelectedShipsLoadingMidLevelOption = _chosenMidLevelShipsOption;
                    //ConfigData.SelectedEnemyShipTypes = _chosenEnemyShipTypes;

                    if (_chosenLevel != null)
                    {
                        ConfigData.LevelOptions = (LevelOptions)_chosenLevel.Clone();
                        Debug.Log($"ConfigData.LevelOptions is {_chosenLevel.Name}");
                        ConfigData.IsUserLoadingCustomEnemySquads = true;

                        // add the sqauds
                        ConfigData.IsUserLoadingCustomSquads = true;
                        _chosenSquads.ForEach((chosenSquad) =>
                        {
                            //Debug.Log($"Chose {chosenSquad.Name} for level");
                            ConfigData.LevelOptions.ChosenSquads.Add((SavedSquad)chosenSquad.Clone());
                        });
                        //Debug.Log($"ConfigData.LevelOption.ChosenSquads: {Utilities.ListToString(ConfigData.LevelOptions.ChosenSquads)}");
                    }
                    else if (!ConfigData.ChooseRandomLevel)
                    {
                        if (_chosenOpposingForceOption == 0) // [alert] order needs to be changed
                        {
                            _enemySquadGenerationCount = 4;
                        }
                        else if (_chosenOpposingForceOption == 1)
                        {
                            _enemySquadGenerationCount = 8;
                        }
                        else if (_chosenOpposingForceOption == 2)
                        {
                            _enemySquadGenerationCount = 12;
                        }
                        else if (_chosenOpposingForceOption == 3)
                        {
                            // Player chooses custom enemy squads
                            ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerSecondSide;
                            ConfigData.IsUserLoadingCustomEnemySquads = true;
                            _nextScene = "Squad Maker";
                            SetLevelOptions();
                            Invoke(nameof(LoadScene), .25f);
                            return;
                        }
                        else if (_chosenOpposingForceOption == 4) // Swarms only
                        {
                            if (ConfigData.Configuration.SquadMakerSecondSide == ConfigData.Configuration.BeeSide)
                            {
                                ConfigData.BeeShipTypes = ConfigData.UserProgressData.VisibleBeeShipTypes.Intersect(ConfigData.BeeSwarmShips).ToHashSet();
                            }
                            else
                            {
                                ConfigData.HumanShipTypes = ConfigData.UserProgressData.VisibleHumanShipTypes.Intersect(ConfigData.HumanSwarmShips).ToHashSet();
                            }
                        }
                        else if (_chosenOpposingForceOption == 5) // Powerful ships only
                        {
                            if (ConfigData.Configuration.SquadMakerSecondSide == ConfigData.Configuration.BeeSide)
                            {
                                ConfigData.BeeShipTypes = ConfigData.UserProgressData.VisibleBeeShipTypes.Intersect(ConfigData.BeePowerfulShips).ToHashSet();
                            }
                            else
                            {
                                ConfigData.HumanShipTypes = ConfigData.UserProgressData.VisibleHumanShipTypes.Intersect(ConfigData.HumanPowerfulShips).ToHashSet();
                                Debug.Log($"Choosing human powerful ships: {ConfigData.HumanShipTypes.ToList()}");
                            }
                        }

                        SetLevelOptions();

                    }
                    else // a random level has been chosen and therefore custom enemy squads associated with that level will be loaded
                    {
                        Debug.Log($"A random level has been chosen");
                        ConfigData.IsUserLoadingCustomEnemySquads = true;
                        List<LevelOptions> possibleLevels = ConfigData.GetLevelData().GetLevels().Where((level) => level.Side == ConfigData.Configuration.AISide).ToList();
                        ConfigData.LevelOptions = (LevelOptions)possibleLevels[Utilities.RandomInt(possibleLevels.Count)].Clone();
                        ConfigData.LevelOptions.ChosenSquads = _chosenSquads;
                    }


                }
                else if (ConfigData.LevelOptions != null)
                {
                    Debug.Log($"Has level options and is choosing custom enemy squads from squad maker");
                    ConfigData.LevelOptions.EnemyShipTypeOption = 0;
                    _chosenSquads.ForEach((chosenSquad) =>
                    {
                        ConfigData.LevelOptions.EnemySquads.Add(chosenSquad.ConvertToUnsavedSquad());

                        if (ConfigData.LevelOptions.EnemyReinforcementsOption != 0)
                        {
                            ConfigData.LevelOptions.EnemyReinforcements.Add(chosenSquad.ConvertToUnsavedSquad());
                        }
                    });
                }
                else
                {
                    Debug.Log($"Does not have level options and is choosing custom enemy squads from squad maker");
                }

            }
            else
            {
                Debug.Log($"Randomized opposing side");
            }

            //ConfigData.SquadsChosenForLevel.ForEach((s) => Debug.Log(s.ToString()));
            _nextScene = "Hivemind Training";
            Invoke(nameof(LoadScene), .5f);
            //SceneManager.LoadSceneAsync("Training Room One Screen", LoadSceneMode.Single); // [alert] this should go to the actual level based on the level number
            //SceneManager.LoadSceneAsync("RL Tiny Box", LoadSceneMode.Single); // [alert] [rl-training]
        }
        private void SetLevelOptions(){
            ConfigData.IsUserLoadingCustomSquads = true;

            //Debug.Log($"Setting level options for configdata");
            ConfigData.LevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}", _chosenMapOption, null, _chosenAsteroidsOption, _chosenFogOfWarOption, _chosenMiningOption, false, true, -1, _chosenEnemyReinforcementsOption, ConfigData.StandardReinforcementsDelay, _chosenEnemyShipTypes, _enemySquadGenerationCount, new List<SavedSquad>(), new List<SavedSquad>(), new List<int>(), "", _chosenSquads, Vector2.zero, Vector2.zero);
        }

        public void ChangeOpposingForceDropdown(int option)
        {
            //TMP_Dropdown dropdown = OpposingForcePresetDropdown.GetComponentInChildren<TMP_Dropdown>();
            _chosenOpposingForceOption = option;

            if (option == 3)
            {
                NextButton.SetActive(true);
                StartButton.SetActive(false);
            }
            else
            {
                NextButton.SetActive(false);
                StartButton.SetActive(true);
            }

            //Debug.Log($"User chose {dropdown.options[option].text}, {_chosenOpposingForceOption}");
        }
        public void ChangeLevel(int option)
        {

            if (option > 1) // a level was chosen
            {
                LoadLevel(option);
                //Debug.Log($"option chosen: {option}");
                
            }
            else if (LevelTitleContainer.activeSelf) // a level was not chosen but was previously shown
            {
                _chosenLevel = null;
                ToggleLevelDetails(false); // hide the level
                ToggleLevelOptions(option == 1); // either show or hide the level options
                _capacity = ConfigData.StartingSettings.SupplyCapacity[Side - 1];
            }
            else // a level was not chosen and was not previously shown
            {
                ToggleLevelOptions(option == 1);
            }

            ConfigData.ChooseRandomLevel = option == 0;

        }
        public void ChangeObstaclesDropdown(int option)
        {
            _chosenObstacleOption = option - 1;
        }
        public void ChangeAsteroidsDropdown(int option)
        {
            _chosenAsteroidsOption = option - 1;
        }
        public void ChangeMapDropdown(int option)
        {
            _chosenMapOption = option - 1;
        }
        public void ChangeFogOfWarDropdown(int option)
        {
            _chosenFogOfWarOption = option - 1;
        }
        public void ChangeMiningDropdown(int option)
        {
            _chosenMiningOption = option - 1;
        }
        public void ChangeShipsLoadingMidLevelDropdown(int option)
        {
            _chosenEnemyReinforcementsOption = option - 1;
        }
        public void ChangeEnemyShipTypes(int option)
        {
            _chosenEnemyShipTypes = option - 1;
            //Debug.Log($"Option: {_chosenEnemyShipTypes}"); 
        }

    }
}