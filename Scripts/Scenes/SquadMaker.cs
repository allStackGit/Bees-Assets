
using Assets.Scripts.Data;
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
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Assets.Scripts.Scenes
{
    public class SquadMaker : Scene
    {
        public int Side;

        public GameObject 
            BargeDragIcon, BeaconDragIcon, CarrierDragIcon, CruiserDragIcon, DreadnoughtDragIcon, DroneDragIcon,
            FactoryDragIcon, FireShipDragIcon, FlagshipDragIcon, FrigateDragIcon, GunshipDragIcon, ScoutDragIcon,
            StrikerDragIcon, WarpGateDragIcon,

            BeehiveDragIcon, BumblebeeDragIcon, CarpenterBeeDragIcon, HoneybeeDragIcon, HornetDragIcon, LeafcutterDragIcon, QueenDragIcon,
            WaspDragIcon, YellowJacketDragIcon,

            BargeShipIcon, BeaconShipIcon, CarrierShipIcon, CruiserShipIcon, DreadnoughtShipIcon, DroneShipIcon,
            FactoryShipIcon, FireShipShipIcon, FlagshipShipIcon, FrigateShipIcon, GunshipShipIcon, ScoutShipIcon,
            StrikerShipIcon, WarpGateShipIcon,

            BeehiveShipIcon, BumblebeeShipIcon, CarpenterBeeShipIcon, HoneybeeShipIcon, HornetShipIcon, LeafcutterShipIcon, QueenShipIcon,
            WaspShipIcon, YellowJacketShipIcon,

            BargeFleetLabel, BeaconFleetLabel, CarrierFleetLabel, CruiserFleetLabel, DreadnoughtFleetLabel, DroneFleetLabel,
            FactoryFleetLabel, FireShipFleetLabel, FlagshipFleetLabel, FrigateFleetLabel, GunshipFleetLabel, ScoutFleetLabel,
            StrikerFleetLabel, WarpGateFleetLabel,

            BeehiveFleetLabel, BumblebeeFleetLabel, CarpenterBeeFleetLabel, HoneybeeFleetLabel, HornetFleetLabel, LeafcutterFleetLabel, QueenFleetLabel, 
            WaspFleetLabel, YellowJacketFleetLabel,

            SquadMakerSupplyCapacityLabel, ChosenSquadsSupplyCapacityLabel, Tooltip, TooltipText, ColorPicker, SavedSquadList,
            ChosenSquadList, SavedSquadPrefab, ChosenSquadPrefab,
            
            SquadActionBox, DeadShipBox, DropZone, DropBox, DragStatusBox, ShipInfoBox, ShipInfoBoxTitle, ShipInfoBoxDetails, ShipInfoBoxIcon, SquadInfoBox,
            SquadInfoBoxTitle, SquadInfoBoxDetails, SquadInfoBoxIcon, ShipStatsBox, ShipStatsBoxDetails, SquadNameInput, ShipNameInput,
            SquadShipCount, SquadShipCountLabel, SquadColorLabel, SquadColorPickerButton, NextButton, StartButton, OpposingForceLabel, OpposingForcePresetDropdown;

        public Dialogue DeleteSquadConfirmation, ClearSquadConfirmation, LoadSquadConfirmation, ChooseSquadConfirmation, UnchooseSquadConfirmation, OverCapacityAlert, NoChosenSquadsAlert,
            ChoosingUnsavedSquadAlert, ChoosingDeadSquadAlert, GoBackConfirmation, SquadSavingStatus;

        public Sprite
            BargeSprite, BeaconSprite, CarrierSprite, CruiserSprite, DreadnoughtSprite, DroneSprite, FactorySprite, FireShipSprite, FlagshipSprite, FrigateSprite,
            GunshipSprite, ScoutSprite, StrikerSprite, WarpGateSprite,

            BeehiveSprite, BumblebeeSprite, CarpenterBeeSprite, HoneybeeSprite, HornetSprite, LeafcutterSprite, QueenSprite,
            WaspSprite, YellowJacketSprite;

        public Sprite
            BargeGameSprite, CarrierGameSprite, CruiserGameSprite, CruiserCannonGameSprite, DreadnoughtGameSprite, FactoryGameSprite, FactoryAnimationSprite, FireShipGameSprite, FlagshipGameSprite, FrigateGameSprite,
            GunshipGameSprite, ScoutGameSprite, WarpGateGameSprite, WarpGateAnimationSprite, WarpGateAnimationLoopSprite;

        public Canvas DragCanvas;
        public Vector2 TooltipOffset, ShipStatsBoxOffset, ScreenScaleFactor, ReferenceScreenSize;
        public SquadActionBox ActionBox = null;
        public bool IsRandomizedOpposingSide;



        private Dictionary<string, GameObject> _dragIconTypes = new Dictionary<string, GameObject>();
        private Dictionary<string, Sprite> _spriteTypes = new Dictionary<string, Sprite>();
        private Dictionary<string, List<Sprite>> _shipPartSprites = new Dictionary<string, List<Sprite>>();
        private Dropper _dropper;
        private List<GameObject> _deadShipBoxes = new List<GameObject>();
        private List<SavedSquad> _chosenSquads = new List<SavedSquad>();
        private List<FleetShip> _fleetList = null;
        private List<string> _shipTypes = new List<string>();
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
        private string _nextScene = "";

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


            _fleetList = ConfigData.AllShips.GetAvailableShips();
            ConfigData.AllShips.ReplaceDeadSquadShips();
            SetupFleetList();
            SetupSavedSquadsList();

            // turn squad labels red for all squads that still have dead ships
            ConfigData.AllShips.GetSavedSquadsBySide(Side).ForEach((squad) =>
            {
                if (squad.HasDeadShips)
                {
                    //Debug.Log($"{squad.Name} still has dead ships");
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}"));
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>());
                    //Debug.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color);
                    GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad"); ;
                }
            });
            //Debug.Log("Finalized the page");
        }
        private void Setup()
        {
            //Debug.Log($"Squad Maker Setup called");
            // Universal pre setup
            _dropper = new Dropper(this);
            Side = ConfigData.SquadMakerSide;
            //ConfigData.SetupSceneManagement(SceneManagement.GetComponent<SceneManagement>());
            ScreenScaleFactor = new Vector2(ConfigData.ScreenWidth / ReferenceScreenSize.x, ConfigData.ScreenHeight / ReferenceScreenSize.y);

            if (Side == ConfigData.Configuration.SquadMakerFirstSide)
            {
                ConfigData.SquadsChosenForLevel.Clear();
                NextButton.SetActive(true);
                StartButton.SetActive(false);
            }
            else
            {
                NextButton.SetActive(false);
                StartButton.SetActive(true);
            }

            // Make Dialogues
            DeleteSquadConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, ConfigData.Configuration.DeleteSquadConfirmation,
                new List<string>() {ConfigData.Configuration.Yes, ConfigData.Configuration.No}, new List<UnityAction>() {DeleteCurrentSquad});

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
                Debugger.Exception($"Side ({Side}) does not match Bee Side ({ConfigData.Configuration.BeeSide}) or Human Side ({ConfigData.Configuration.HumanSide})");
            }
            if (Side != ConfigData.Configuration.UserSide)
            {
                SetupForOpposingSide();
            }
            else if (IsRandomizedOpposingSide)
            {
                SkipOpposingSideSetup();
            }

            _shipPartSprites["Barge"] = new List<Sprite> { BargeGameSprite };
            //_shipPartSprites["Beacon"] = new List<Sprite> { BeaconGameSprite }; // no beacon because we won't be caching sprites for scout ships
            _shipPartSprites["Carrier"] = new List<Sprite> { CarrierGameSprite };
            _shipPartSprites["Cruiser"] = new List<Sprite> { CruiserGameSprite, CruiserCannonGameSprite };
            _shipPartSprites["Dreadnought"] = new List<Sprite> { DreadnoughtGameSprite };
            //_shipPartSprites["Drone"] = new List<Sprite> { DroneGameSprite }; // no drone because we won't be caching sprites for carrier ships
            _shipPartSprites["Factory"] = new List<Sprite> { FactoryGameSprite, FactoryAnimationSprite };
            _shipPartSprites["Fire Ship"] = new List<Sprite> { FireShipGameSprite };
            _shipPartSprites["Flagship"] = new List<Sprite> { FlagshipGameSprite };
            _shipPartSprites["Frigate"] = new List<Sprite> { FrigateGameSprite };
            _shipPartSprites["Gunship"] = new List<Sprite> { GunshipGameSprite };
            _shipPartSprites["Scout"] = new List<Sprite> { ScoutGameSprite };
            //_shipPartSprites["Striker"] = new List<Sprite> { StrikerGameSprite }; // no striker because we won't be caching sprites for carrier ships
            _shipPartSprites["Warp Gate"] = new List<Sprite> { WarpGateGameSprite, WarpGateAnimationSprite, WarpGateAnimationLoopSprite };
            
            // No bee sprites because those don't change colors



            // Post setup
            //Debug.Log("Post setup");
            UpdateSquadMakerSupplyLabel();
            UpdateChosenSquadsSupplyLabel();
            UpdateSquadShipCounter();

        }
        private void SetupForOpposingSide()
        {

            // Hide the "Choose Opposing Force" options and extend the squad list size
            OpposingForceLabel.SetActive(false);
            OpposingForcePresetDropdown.SetActive(false);
            RectTransform squadListRect = ChosenSquadList.transform.parent.parent.GetComponent<RectTransform>();
            squadListRect.sizeDelta = new Vector2(squadListRect.sizeDelta.x, squadListRect.sizeDelta.y + 200);
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

            _dragIconTypes.Add("Beehive", BeehiveDragIcon);
            _dragIconTypes.Add("Bumblebee", BumblebeeDragIcon);
            _dragIconTypes.Add("Carpenter Bee", CarpenterBeeDragIcon);
            _dragIconTypes.Add("Honeybee", HoneybeeDragIcon);
            _dragIconTypes.Add("Hornet", HornetDragIcon);
            _dragIconTypes.Add("Leafcutter", LeafcutterDragIcon);
            _dragIconTypes.Add("Queen", QueenDragIcon);
            _dragIconTypes.Add("Wasp", WaspDragIcon);
            _dragIconTypes.Add("Yellow Jacket", YellowJacketDragIcon);

            _spriteTypes.Add("Beehive", BeehiveSprite);
            _spriteTypes.Add("Bumblebee", BumblebeeSprite);
            _spriteTypes.Add("Carpenter Bee", CarpenterBeeSprite);
            _spriteTypes.Add("Honeybee", HoneybeeSprite);
            _spriteTypes.Add("Hornet", HornetSprite);
            _spriteTypes.Add("Leafcutter", LeafcutterSprite);
            _spriteTypes.Add("Queen", QueenSprite);
            _spriteTypes.Add("Wasp", WaspSprite);
            _spriteTypes.Add("Yellow Jacket", YellowJacketSprite);

            ActionBox = SquadActionBox.GetComponent<SquadActionBox>();
            ActionBox.Setup(this, EventSystem, ConfigData.Configuration.BeeSide);

            _shipTypes = ConfigData.StartingSettings.BeeShipTypes;

            StrikerFleetLabel.transform.parent.gameObject.SetActive(false);
            DroneFleetLabel.transform.parent.gameObject.SetActive(false);
            BeaconFleetLabel.transform.parent.gameObject.SetActive(false);
            SquadColorLabel.SetActive(false);
            SquadColorPickerButton.SetActive(false);



        }
        private void SetupForHumans()
        {
            //Debug.Log("Setting up for Humans!");
            _colorPicker = ColorPicker.GetComponent<ColorPicker>();

            _dragIconTypes.Add("Barge", BargeDragIcon);
            _dragIconTypes.Add("Beacon", BeaconDragIcon);
            _dragIconTypes.Add("Carrier", CarrierDragIcon);
            _dragIconTypes.Add("Cruiser", CruiserDragIcon);
            _dragIconTypes.Add("Dreadnought", DreadnoughtDragIcon);
            _dragIconTypes.Add("Drone", DroneDragIcon);
            _dragIconTypes.Add("Factory", FactoryDragIcon);
            _dragIconTypes.Add("Fire Ship", FireShipDragIcon);
            _dragIconTypes.Add("Flagship", FlagshipDragIcon);
            _dragIconTypes.Add("Frigate", FrigateDragIcon);
            _dragIconTypes.Add("Gunship", GunshipDragIcon);
            _dragIconTypes.Add("Scout", ScoutDragIcon);
            _dragIconTypes.Add("Striker", StrikerDragIcon);
            _dragIconTypes.Add("Warp Gate", WarpGateDragIcon);

            _spriteTypes.Add("Barge", BargeSprite);
            _spriteTypes.Add("Beacon", BeaconSprite);
            _spriteTypes.Add("Carrier", CarrierSprite);
            _spriteTypes.Add("Cruiser", CruiserSprite);
            _spriteTypes.Add("Dreadnought", DreadnoughtSprite);
            _spriteTypes.Add("Drone", DroneSprite);
            _spriteTypes.Add("Factory", FactorySprite);
            _spriteTypes.Add("Fire Ship", FireShipSprite);
            _spriteTypes.Add("Flagship", FlagshipSprite);
            _spriteTypes.Add("Frigate", FrigateSprite);
            _spriteTypes.Add("Gunship", GunshipSprite);
            _spriteTypes.Add("Scout", ScoutSprite);
            _spriteTypes.Add("Striker", StrikerSprite);
            _spriteTypes.Add("Warp Gate", WarpGateSprite);

            ActionBox = SquadActionBox.GetComponent<SquadActionBox>();
            ActionBox.Setup(this, EventSystem, ConfigData.Configuration.HumanSide);

            _shipTypes = ConfigData.StartingSettings.HumanShipTypes;
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
                    case "Barge":
                        shipLabel = BargeFleetLabel;
                        break;
                    case "Beacon":
                        shipLabel = BeaconFleetLabel;
                        break;
                    case "Carrier":
                        shipLabel = CarrierFleetLabel;
                        break;
                    case "Cruiser":
                        shipLabel = CruiserFleetLabel;
                        break;
                    case "Dreadnought":
                        shipLabel = DreadnoughtFleetLabel;
                        break;
                    case "Drone":
                        shipLabel = DroneFleetLabel;
                        break;
                    case "Factory":
                        shipLabel = FactoryFleetLabel;
                        break;
                    case "Fire Ship":
                        shipLabel = FireShipFleetLabel;
                        break;
                    case "Flagship":
                        shipLabel = FlagshipFleetLabel;
                        break;
                    case "Frigate":
                        shipLabel = FrigateFleetLabel;
                        break;
                    case "Gunship":
                        shipLabel = GunshipFleetLabel;
                        break;
                    case "Scout":
                        shipLabel = ScoutFleetLabel;
                        break;
                    case "Striker":
                        shipLabel = StrikerFleetLabel;
                        break;
                    case "Warp Gate":
                        shipLabel = WarpGateFleetLabel;
                        break;


                    case "Beehive":
                        shipLabel = BeehiveFleetLabel;
                        break;
                    case "Bumblebee":
                        shipLabel = BumblebeeFleetLabel;
                        break;
                    case "Carpenter Bee":
                        shipLabel = CarpenterBeeFleetLabel;
                        break;
                    case "Honeybee":
                        shipLabel = HoneybeeFleetLabel;
                        break;
                    case "Hornet":
                        shipLabel = HornetFleetLabel;
                        break;
                    case "Leafcutter":
                        shipLabel = LeafcutterFleetLabel;
                        break;
                    case "Queen":
                        shipLabel = QueenFleetLabel;
                        break;
                    case "Wasp":
                        shipLabel = WaspFleetLabel;
                        break;
                    case "Yellow Jacket":
                        shipLabel = YellowJacketFleetLabel;
                        break;
                }
                //Debug.Log($"About to access the parent, {shipLabel}");
                //Debug.Log($"About to access the parent, {shipLabel.transform}");
                //Debug.Log($"About to access the parent, {shipLabel.transform.parent}");

                Transform parent = shipLabel.transform.parent;
                List<FleetShip> availableShips = ConfigData.AllShips.GetAvailableShipsOfType(type);
                List<FleetShip> visibleShips = ConfigData.AllShips.GetVisibleAndAliveShipsOfType(type);

                // if ship type has any visible ships
                if (visibleShips.Any())
                {
                    //Debug.Log($"Setting the ship count for {type}");
                    // get the count of the ship type and update the label
                    TMP_Text labelText = shipLabel.GetComponentInChildren<TMP_Text>();
                    labelText.text = $"({availableShips.Count})";

                    if (parent != null)
                    {
                        parent.gameObject.SetActive(true);
                    }
                }
                else // if not, set the label to inactive
                {
                    Debug.Log($"There were no visible ships for {type}");
                    parent.gameObject.SetActive(false);
                }
            });
        }
        private void SetupSavedSquadsList()
        {
            //Debug.Log("Setting up the list of saved squads");
            ConfigData.AllShips.GetSavedSquads().Where((s) => s.Side == Side).ToList().ForEach((savedSquad) =>
            {
                AddSavedSquadToList(savedSquad);
            });
        }


        // Dialogues
        public void ConfirmDeleteSquad()
        {
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
                SavedSquad squad = ConfigData.AllShips.GetSavedSquads().Where((s) => s.Id == id).First();
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
            int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
            SavedSquad squad = ConfigData.AllShips.GetSavedSquads().Where((s) => s.Id == id).First();
            _squadToUnchoose = squad;


            // Change these lines in order to show the confirmation dialogue and then do the action, or to just do the action
            UnchooseSquad();
            //UnchooseSquadConfirmation.Show();
        }
        public void ConfirmStartLevel()
        {
            //Debug.Log("Starting level!");
            int capacity = ConfigData.StartingSettings.SupplyCapacity[Side - 1];
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
        private GameObject GetShipIconContainer(string shipType)
        {
            switch(shipType)
            {
                case "Barge":
                    return BargeShipIcon;
                case "Beacon":
                    return BeaconShipIcon;
                case "Carrier":
                    return CarrierShipIcon;
                case "Cruiser":
                    return CruiserShipIcon;
                case "Dreadnought":
                    return DreadnoughtShipIcon;
                case "Factory":
                    return FactoryShipIcon;
                case "Fire Ship":
                    return FireShipShipIcon;
                case "Flagship":
                    return FlagshipShipIcon;
                case "Frigate":
                    return FrigateShipIcon;
                case "Gunship":
                    return GunshipShipIcon;
                case "Scout":
                    return ScoutShipIcon;
                case "Warp Gate":
                    return WarpGateShipIcon;
                case "Drone":
                    return DroneShipIcon;
                case "Striker":
                    return StrikerShipIcon;

                case "Beehive":
                    return BeehiveShipIcon;
                case "Bumblebee":
                    return BumblebeeShipIcon;
                case "Carpenter Bee":
                    return CarpenterBeeShipIcon;
                case "Honeybee":
                    return HoneybeeShipIcon;
                case "Hornet":
                    return HornetShipIcon;
                case "Leafcutter":
                    return LeafcutterShipIcon;
                case "Queen":
                    return QueenShipIcon;
                case "Wasp":
                    return WaspShipIcon;
                case "Yellow Jacket":
                    return YellowJacketShipIcon;
            }
            return null;
        }
        private void AddSavedSquadToList(SavedSquad savedSquad)
        {
            // instantiate a squad label
            SavedSquadPrefab.SetActive(true);
            GameObject squadLabel =  Instantiate(SavedSquadPrefab);
            squadLabel.name = $"Saved Squad - {savedSquad.Name} #{savedSquad.Id}";
            string shipType = savedSquad.GetMostValuableShip().GetFleetShip().Type;


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

                int[] changeablePixels = Utilities.SetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
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
            string shipType = chosenSquad.GetMostValuableShip().GetFleetShip().Type;


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

                int[] changeablePixels = Utilities.SetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
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
            string shipType = savedSquad.GetMostValuableShip().GetFleetShip().Type;

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
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
                squadIconImage.sprite = Utilities.SetImageColor(savedSquad.Color, squadIconImage.sprite, changeablePixels);
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
            int capacity = ConfigData.StartingSettings.SupplyCapacity[Side - 1];
            GameObject container = ChosenSquadsSupplyCapacityLabel.transform.parent.gameObject;
            TMP_Text text = ChosenSquadsSupplyCapacityLabel.GetComponentInChildren<TMP_Text>();


            text.text = $"Supply Capacity: {supply.ToString("N0")} / {capacity.ToString("N0")}";
            if (supply > capacity)
            {
                container.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
            }
            else
            {
                container.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("supply-capacity-label");
            }
        }
        public void UpdateShipCounter(FleetShip fleetShip)
        {
            GameObject inventoryContainer = GameObject.FindObjectsOfType<GameObject>(true).ToList().Find((gameObject) => gameObject.name == $"{fleetShip.Type} Inventory Ship");
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
        private string ValidateInputString(string str)
        {
            return Regex.Replace(str, @"[^a-zA-Z0-9\-\s!@#%&*_+=:'.]", "");
            //Debug.Log($"Unvalidated string: {name}, replaced string {valid}");
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
            SavedSquad savedSquad = ConfigData.AllShips.GetSavedSquad(_currentSquad.Id);
            _currentSquad.Id = -1; // make sure it doesn't match any existing squad

            // remove all icons from the screen
            GetDropper().RemoveDragIcons();

            // clear name, drag icon, and color
            _nameText = "";
            _squadColor = ConfigData.UnsetColor;
            SquadNameInput.GetComponent<TMP_InputField>().text = "";

            if (savedSquad != null)
            {
                List<string> updatedShipTypes = new List<string>();
                savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    string shipType = ship.ShipType;
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
            if (HasCurrentSquad)
            {
                _currentSquad.OrientSquad();
                //Debug.Log($"Saving {_currentUnsavedSquad.Name}");

                //Debug.Log($"Squad starting position: {_currentUnsavedSquad.StartingPosition}");

                if (ConfigData.AllShips.DoesSquadExist(_currentSquad.Id))
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
            int squadId = _currentSquad.Id;
            SavedSquad savedSquad = ConfigData.AllShips.GetSavedSquad(squadId);
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
                ConfigData.AllShips.RemoveSquad(_currentSquad);

                // remove the entry from the squad ui list
                RemoveSavedSquadFromList(_currentSquad);

                // clear the squad maker
                ClearUnsavedSquad();

                // save the squads
                ConfigData.AllShips.SaveSquadData();
            }
        }
        public void SaveNewSquad()
        {
            //Debug.Log("New squad, does not exist yet");
            _currentSquad.Id = ConfigData.GetUserProgressData().GetNextSavedSquadId();
            if (_currentSquad.Name == "")
            {
                _currentSquad.Name = $"Squadron #{_currentSquad.Id}";
            }
            ConfigData.AllShips.AddSquad(_currentSquad);
            AddSavedSquadToList(ConfigData.AllShips.GetSavedSquads().Last());

            ConfigData.AllShips.SaveSquadData();
            ConfigData.AllShips.SaveFleetData();
            SquadSavingStatus.Show();
            StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipPartSprites, SquadSavingStatus));
            ClearUnsavedSquad();


        }
        public void SaveExistingSquad()
        {
            //Debug.Log($"Squad does exist, replacing old squad with {_currentSquad.Name}");
            SavedSquad oldSavedSquad = ConfigData.AllShips.GetSavedSquad(_currentSquad.Id);

            UpdateSavedSquadInList(GameObject.Find($"Saved Squad - {oldSavedSquad.Name} #{oldSavedSquad.Id}"), _currentSquad);
            List<SavedSquad> savedSquads = ConfigData.AllShips.GetSavedSquads();
            int replacementIndex = savedSquads.IndexOf(oldSavedSquad);
            savedSquads[replacementIndex] = (SavedSquad)_currentSquad.Clone();

            ConfigData.AllShips.SaveSquadData();
            ConfigData.AllShips.SaveFleetData();
            if (oldSavedSquad.Color != _currentSquad.Color || oldSavedSquad.GetSquadShips().Count != _currentSquad.GetSquadShips().Count || 
                oldSavedSquad.GetSquadShips().Any((s) => _currentSquad.GetShip(s.GetFleetShip().Id) == null))
            {
                SquadSavingStatus.Show();
                StartCoroutine(Utilities.CacheSquadCustomSprites((SavedSquad)_currentSquad.Clone(), _shipPartSprites, SquadSavingStatus));
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
                //Debug.Log($"Starting Position for {ship.GetFleetShip().Name}: {_currentUnsavedSquad.StartingPosition}, Offset position: {ship.Offset}"); 
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
        public GameObject GetDragIconPrefab(string type)
        {
            return _dragIconTypes.GetValueOrDefault(type);
        }
        public void AutoPlaceShip(string shipType)
        {
            _dropper.AutoPlaceShip(shipType);
        }
        public Dropper GetDropper()
        {
            return _dropper;
        }
        public void SetFormation(string formation)
        {
            Dropper dropper = GetDropper();
            if (HasCurrentSquad && dropper.GetDragIcons().Count > 0)
            {
                _currentSquad.GetSquadShips().Clear();
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
        public void FleetDragStart(string shipType)
        {

            Dropper dropper = GetDropper();
            dropper.PullNewDragIcon(shipType);
            dropper.SetupActiveDragging(Input.mousePosition, false);

        }
        public void FleetDragStart(GameObject label)
        {
            if (label != null)
            {
                int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                FleetShip ship = ConfigData.AllShips.GetFleetShip(id);

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
        public void ShowShipInfo(string ship)
        {
            if (!GetDropper().IsDragging)
            {
                TMP_Text titleText = ShipInfoBoxTitle.GetComponent<TMP_Text>();
                TMP_Text detaislText = ShipInfoBoxDetails.GetComponent<TMP_Text>();
                ShipStatBlock shipInfo = ConfigData.GetShipInfo(ship);

                titleText.text = $"{ship} Details";
                detaislText.text = $"{shipInfo.Description}\n\n" +
                    $"Health: {shipInfo.Health.ToString("N0")}\n" +
                    $"Range: {shipInfo.PrintRange()}\n" +
                    $"Power: {shipInfo.PrintPower()}\n" +
                    $"Rate of Fire: {shipInfo.PrintRateOfFire()}\n" +
                    $"Speed: {shipInfo.Speed}\n"+
                    $"Capacity: {(ship != "Drone" && ship != "Striker" && ship != "Beacon" ? ConfigData.AllShips.GetShipsOfType(ship).First().GetMaxCapacity().ToString("N0") : "N/A")}";

                UnityEngine.UI.Image image = ShipInfoBoxIcon.GetComponent<UnityEngine.UI.Image>();
                image.sprite = _spriteTypes.GetValueOrDefault(ship);
                image.SetNativeSize();
                image.transform.localScale = new Vector3(.1f, .1f, 0);

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
                    int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#")+1));
                    SavedSquad squad = ConfigData.AllShips.GetSavedSquads().Where((s) => s.Id == id).First();
                    SquadStatBlock stats = squad.Stats;
                    //Debug.Log($"Squad ID: {id}");

                    TMP_Text titleText = SquadInfoBoxTitle.GetComponent<TMP_Text>();
                    TMP_Text detaislText = SquadInfoBoxDetails.GetComponent<TMP_Text>();


                    titleText.text = $"{squad.Name}";
                    detaislText.text = $"Commander: {stats.Commander}\n\n" +
                        $"Ships: {(squad.GetSquadShips().Count - squad.GetDeadShips().Count).ToString("N0")} / {squad.GetSquadShips().Count.ToString("N0")} " +
                        $"{(squad.HasDeadShips ? $" <color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(ConfigData.GetUIColor("bad"))}><smallcaps><b>(Unfilled)</b></smallcaps></color>" : "")}\n" +
                        $"Capacity: {squad.GetCapacity().ToString("N0")} / {squad.GetMaxCapacity().ToString("N0")}\n" +
                        $"Battles: {stats.BattlesFought.ToString("N0")}: {stats.BattlesWon}W - {stats.BattlesLost}L     (#{ConfigData.AllShips.GetSquadRanking(squad, "Record")})\n" +
                        $"Damage Done: {stats.DamageDone.ToString("N0")}     (#{ConfigData.AllShips.GetSquadRanking(squad, "DamageDone")})\n" +
                        $"Damage Received: {stats.DamageReceived.ToString("N0")}     (#{ConfigData.AllShips.GetSquadRanking(squad, "DamageReceived")})\n" +
                        $"Kills: {stats.Kills.ToString("N0")}     (#{ConfigData.AllShips.GetSquadRanking(squad, "Kills")})\n" +
                        $"Ships Lost: {stats.ShipsLost.ToString("N0")}     (#{ConfigData.AllShips.GetSquadRanking(squad, "ShipsLost")})\n";

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
                    _currentShipInfo = ConfigData.AllShips.GetFleetShip(id);
                    //Debug.Log($"Squad ID: {id}");
                    //Debug.Log(_currentShipInfo.Name);

                    ShipStatsBoxDetails.GetComponent<TMP_Text>().text = $"Battles: {_currentShipInfo.BattlesFought.ToString("N0")}: {_currentShipInfo.BattlesWon}W - {_currentShipInfo.BattlesLost}L     (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "Record")})\n" +
                        $"Shots Fired: {_currentShipInfo.ShotsFired.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "ShotsFired")})\n" +
                        $"Damage Done: {_currentShipInfo.DamageDone.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "DamageDone")})\n" +
                        $"Damage Received: {_currentShipInfo.DamageReceived.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "DamageReceived")})\n" +
                        $"Kills: {_currentShipInfo.Kills.ToString("N0")}    (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "Kills")})\n" + 
                        $"{(_currentShipInfo.Type == "Carpenter Bee" || _currentShipInfo.Type == "Factory" ? $"Minerals Mined: {_currentShipInfo.MineralsMined.ToString("N0")}  (#{ConfigData.AllShips.GetShipRanking(_currentShipInfo, "Minerals Mined")})" : "\n")}";


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
            name = ValidateInputString(name);
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
            name = ValidateInputString(name);
            if (_currentShipInfo != null)
            {
                _currentShipInfo.Name = name;
            }
            //Debug.Log($"Ship name changed to {name}");
            ShipNameInput.GetComponent<TMP_InputField>().text = name;
        }
        public void OpenColorPicker()
        {
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

            // add the sqauds
            ConfigData.IsUserLoadingCustomSquads = true;
            _chosenSquads.ForEach((chosenSquad) =>
            {
                Debug.Log($"Chose {chosenSquad.Name} for level");
                ConfigData.SquadsChosenForLevel.Add((SavedSquad)chosenSquad.Clone());
            });

            //Debug.Log($"SMS: {ConfigData.SquadMakerSide}, SMFS: {ConfigData.Configuration.SquadMakerFirstSide}, SMSS: {ConfigData.Configuration.SquadMakerSecondSide}");
            // go to next side if you need to
            if (!IsRandomizedOpposingSide)
            {
                ConfigData.BeeShipTypes = ConfigData.Configuration.VisibleBeeShipTypes;
                ConfigData.HumanShipTypes = ConfigData.Configuration.VisibleHumanShipTypes;

                if (Side == ConfigData.Configuration.SquadMakerFirstSide)
                {
                    if (_chosenOpposingForceOption == 0) // [alert] order needs to be changed
                    {
                        ConfigData.Configuration.SquadGenerationCount = 4;
                    }
                    else if (_chosenOpposingForceOption == 1)
                    {
                        ConfigData.Configuration.SquadGenerationCount = 8;
                    }
                    else if (_chosenOpposingForceOption == 2)
                    {
                        ConfigData.Configuration.SquadGenerationCount = 12;
                    }
                    else if (_chosenOpposingForceOption == 3)
                    {
                        // Player chooses custom enemy squads
                        ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerSecondSide;
                        ConfigData.IsUserLoadingCustomEnemySquads = true;
                        _nextScene = "Squad Maker";
                        Invoke(nameof(LoadScene), .25f);
                        return;
                    }
                    else if (_chosenOpposingForceOption == 4)
                    {
                        if (ConfigData.Configuration.SquadMakerSecondSide == ConfigData.Configuration.BeeSide)
                        {
                            ConfigData.BeeShipTypes = ConfigData.Configuration.VisibleBeeShipTypes.Intersect(ConfigData.BeeSwarmShips).ToHashSet();
                        }
                        else
                        {
                            ConfigData.HumanShipTypes = ConfigData.Configuration.VisibleHumanShipTypes.Intersect(ConfigData.HumanSwarmShips).ToHashSet();
                        }
                    }
                    else if (_chosenOpposingForceOption == 5)
                    {
                        if (ConfigData.Configuration.SquadMakerSecondSide == ConfigData.Configuration.BeeSide)
                        {
                            ConfigData.BeeShipTypes = ConfigData.Configuration.VisibleBeeShipTypes.Intersect(ConfigData.BeePowerfulShips).ToHashSet();
                        }
                        else
                        {
                            ConfigData.HumanShipTypes = ConfigData.Configuration.VisibleHumanShipTypes.Intersect(ConfigData.HumanPowerfulShips).ToHashSet();
                            Debug.Log($"Choosing human powerful ships: {ConfigData.HumanShipTypes.ToList()}");
                        }
                    }
                }

            }
            
            //ConfigData.SquadsChosenForLevel.ForEach((s) => Debug.Log(s.ToString()));
            _nextScene = "Hivemind Training";
            Invoke(nameof(LoadScene), .5f);
            //SceneManager.LoadSceneAsync("Training Room One Screen", LoadSceneMode.Single); // [alert] this should go to the actual level based on the level number
            //SceneManager.LoadSceneAsync("RL Tiny Box", LoadSceneMode.Single); // [alert] [rl-training]
        }
        private void LoadScene()
        {
            Debug.Log("Loading scene!");
            SceneManager.LoadSceneAsync(_nextScene, LoadSceneMode.Single);
        }
        public void ChangeOpposingForceDropdown(int option)
        {
            //TMP_Dropdown dropdown = OpposingForcePresetDropdown.GetComponentInChildren<TMP_Dropdown>();
            _chosenOpposingForceOption = option;

            if (option == 0)
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





        private void OnDestroy()
        {
            //Debug.Log("Destroying squad maker scene");
        }
    }
}