
using Assets.Scripts.Data;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using Assets.Scripts.UIComponents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Xml;
using TMPro;
using Unity.VisualScripting;
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
            BargeDragIcon, CarrierDragIcon, CruiserDragIcon, DreadnoughtDragIcon, DroneDragIcon,
            FactoryDragIcon, FireShipDragIcon, FlagshipDragIcon, FrigateDragIcon, GunshipDragIcon, ScoutDragIcon,
            StrikerDragIcon, WarpGateDragIcon,

            BeehiveDragIcon, BumblebeeDragIcon, CarpenterBeeDragIcon, HoneybeeDragIcon, HornetDragIcon, LeafcutterDragIcon, QueenDragIcon,
            WaspDragIcon, YellowJacketDragIcon,

            BargeShipIcon, CarrierShipIcon, CruiserShipIcon, DreadnoughtShipIcon, DroneShipIcon,
            FactoryShipIcon, FireShipShipIcon, FlagshipShipIcon, FrigateShipIcon, GunshipShipIcon, ScoutShipIcon,
            StrikerShipIcon, WarpGateShipIcon,

            BeehiveShipIcon, BumblebeeShipIcon, CarpenterBeeShipIcon, HoneybeeShipIcon, HornetShipIcon, LeafcutterShipIcon, QueenShipIcon,
            WaspShipIcon, YellowJacketShipIcon,

            BargeFleetLabel, CarrierFleetLabel, CruiserFleetLabel, DreadnoughtFleetLabel, DroneFleetLabel,
            FactoryFleetLabel, FireShipFleetLabel, FlagshipFleetLabel, FrigateFleetLabel, GunshipFleetLabel, ScoutFleetLabel,
            StrikerFleetLabel, WarpGateFleetLabel,

            BeehiveFleetLabel, BumblebeeFleetLabel, CarpenterBeeFleetLabel, HoneybeeFleetLabel, HornetFleetLabel, LeafcutterFleetLabel, QueenFleetLabel, 
            WaspFleetLabel, YellowJacketFleetLabel,

            SquadMakerSupplyCapacityLabel, ChosenSquadsSupplyCapacityLabel, Tooltip, TooltipText, ColorPicker, SavedSquadList,
            ChosenSquadList, SavedSquadPrefab, ChosenSquadPrefab,
            
            SquadActionBox, DeadShipBox, DropZone, DropBox, DragStatusBox, ShipInfoBox, ShipInfoBoxTitle, ShipInfoBoxDetails, ShipInfoBoxIcon, SquadInfoBox,
            SquadInfoBoxTitle, SquadInfoBoxDetails, SquadInfoBoxIcon, ShipStatsBox, ShipStatsBoxTitle, ShipStatsBoxDetails, SquadNameInput,
            SquadShipCount, SquadShipCountLabel, SquadColorLabel, SquadColorPickerButton, NextButton, StartButton, OpposingForceLabel, OpposingForcePresetDropdown;

        public Dialogue DeleteSquadConfirmation, ClearSquadConfirmation, LoadSquadConfirmation, ChooseSquadConfirmation, UnchooseSquadConfirmation, OverCapacityAlert, NoChosenSquadsAlert,
            ChoosingUnsavedSquadAlert, ChoosingDeadSquadAlert, GoBackConfirmation;

        public Sprite
            BargeSprite, CarrierSprite, CruiserSprite, DreadnoughtSprite, DroneSprite, FactorySprite, FireShipSprite, FlagshipSprite, FrigateSprite,
            GunshipSprite, ScoutSprite, StrikerSprite, WarpGateSprite,

            BeehiveSprite, BumblebeeSprite, CarpenterBeeSprite, HoneybeeSprite, HornetSprite, LeafcutterSprite, QueenSprite,
            WaspSprite, YellowJacketSprite;

        public Canvas DragCanvas;
        public Vector2 TooltipOffset, ShipStatsBoxOffset, ScreenScaleFactor, ReferenceScreenSize;
        public SquadActionBox ActionBox = null;



        private Dictionary<string, GameObject> _dragIconTypes = new Dictionary<string, GameObject>();
        private Dictionary<string, Sprite> _spriteTypes = new Dictionary<string, Sprite>();
        private Dropper _dropper;
        private List<GameObject> _deadShipBoxes = new List<GameObject>();
        private List<SavedSquad> _chosenSquads = new List<SavedSquad>();
        private List<FleetShip> _fleetList = null;
        private List<string> _shipTypes = new List<string>();
        private SavedSquad _currentSquad = null;
        private SavedSquad _squadToLoad = null;
        private SavedSquad _squadToChoose = null;
        private SavedSquad _squadToUnchoose = null;
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
            //Debugger.Log("Starting squad maker");
            //Debugger.Log($"GameObject: {gameObject}, TimeScale: {Time.timeScale}");
            InvokeRepeating(nameof(UpdateDimensions), 1, 1f);
        }
        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();
            Setup();


            _fleetList = ConfigData.Ships.GetAvailableShips();
            SetupFleetList();
            SetupSavedSquadsList();

            // turn squad labels red for all squads that still have dead ships
            ConfigData.Ships.GetSavedSquadsBySide(Side).ForEach((squad) =>
            {
                if (squad.HasDeadShips)
                {
                    //Debugger.Log($"{squad.Name} still has dead ships");
                    //Debugger.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}"));
                    //Debugger.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>());
                    //Debugger.Log(GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color);
                    GameObject.Find($"Saved Squad - {squad.Name} #{squad.Id}").GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad"); ;
                }
            });
            //Debugger.Log("Finalized the page");
        }
        private void Setup()
        {
            //Debugger.Log($"Squad Maker Setup called");
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

            // Post setup
            //Debugger.Log("Post setup");
            UpdateSquadMakerSupplyLabel();
            UpdateChosenSquadsSupplyLabel();
            UpdateSquadShipCounter();

        }
        private void SetupForBees()
        {
            //Debugger.Log($"Setting up for Bees!");
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

            _shipTypes = ConfigData.StartingSettings.BeeShipTypes;

            StrikerFleetLabel.transform.parent.gameObject.SetActive(false);
            DroneFleetLabel.transform.parent.gameObject.SetActive(false);
            SquadColorLabel.SetActive(false);
            SquadColorPickerButton.SetActive(false);
            OpposingForceLabel.SetActive(false);
            OpposingForcePresetDropdown.SetActive(false);

            RectTransform squadListRect = ChosenSquadList.transform.parent.parent.GetComponent<RectTransform>();
            squadListRect.sizeDelta = new Vector2(squadListRect.sizeDelta.x, squadListRect.sizeDelta.y + 200);
        }
        private void SetupForHumans()
        {
            //Debugger.Log("Setting up for Humans!");
            _colorPicker = ColorPicker.GetComponent<ColorPicker>();

            _dragIconTypes.Add("Barge", BargeDragIcon);
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
            ActionBox.Setup(this, EventSystem);

            _shipTypes = ConfigData.StartingSettings.HumanShipTypes;
            //Debugger.Log("End of human setup");
        }
        private void UpdateDimensions()
        {
            //Debugger.Log("Updating dimensions");
            if (Screen.width != ConfigData.ScreenWidth || Screen.height != ConfigData.ScreenHeight)
            {
                ConfigData.ScreenWidth = Screen.width;
                ConfigData.ScreenHeight = Screen.height;
                //Debugger.Log("Updated the base world point");
                ScreenScaleFactor = new Vector2(ConfigData.ScreenWidth / ReferenceScreenSize.x, ConfigData.ScreenHeight / ReferenceScreenSize.y);
                Debugger.Log($"The screen scale factor is {ScreenScaleFactor} and one world unit is {Utilities.WorldUnitsToScreenPixels(Vector2.one, Camera)} pixels in size");
                if (HasColorPicker)
                {
                    _colorPicker.SetScreenScaleFactor();
                }
                List<DragIcon> dragIcons = GetDropper().GetDragIcons();
                if (_currentSquad != null)
                {
                    bool hasChanged = _currentSquad.HasChanged;
                    _currentSquad.GetShips().Clear();
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
            //Debugger.Log($"Setting up the fleet list, {ConfigData.StartingSettings.HumanShipTypes.Count}");

            // loop through all ship types
            _shipTypes.ForEach(type =>
            {
                //Debugger.Log($"Getting fleet ships for {type}");
                GameObject shipLabel = null;
                switch (type)
                {
                    case "Barge":
                        shipLabel = BargeFleetLabel;
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
                //Debugger.Log($"About to access the parent, {shipLabel}");
                //Debugger.Log($"About to access the parent, {shipLabel.transform}");
                //Debugger.Log($"About to access the parent, {shipLabel.transform.parent}");

                Transform parent = shipLabel.transform.parent;
                List<FleetShip> availableShips = ConfigData.Ships.GetAvailableShipsOfType(type);
                List<FleetShip> visibleShips = ConfigData.Ships.GetVisibleAndAliveShipsOfType(type);

                // if ship type has any visible ships
                if (visibleShips.Any())
                {
                    //Debugger.Log($"Setting the ship count for {type}");
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
                    //Debugger.Log($"There were no visible ships for {type}");
                    parent.gameObject.SetActive(false);
                }
            });
        }
        private void SetupSavedSquadsList()
        {
            //Debugger.Log("Setting up the list of saved squads");
            ConfigData.Ships.GetSavedSquads().Where((s) => s.Side == Side).ToList().ForEach((savedSquad) =>
            {
                AddSavedSquadToList(savedSquad);
            });
        }


        // Dialogues
        public void ConfirmDeleteSquad()
        {
            if (_currentSquad != null)
            {
                if (_currentSquad.HasBeenSaved)
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
                //Debugger.Log("Already clicked once, marking double click, choosing squad");
                _doubleClick = true;
                ConfirmChooseSquad(label);
            }
            else // first click
            {
                //Debugger.Log("First click");
                //Debugger.Log(TimeScale);
                _singleClick = true;
                int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
                SavedSquad squad = ConfigData.Ships.GetSavedSquads().Where((s) => s.Id == id).First();
                _squadToLoad = squad;
                Invoke(nameof(ResetSingleClick), .5f);
            }

        }
        public void ResetSingleClick()
        {
            if (!_doubleClick) // has not double clicked and opened the confirm choose squad label
            {
                //Debugger.Log("No double click, loading squad");
                ConfirmLoadSquad();
            }
            //Debugger.Log("Resetting click");
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
        public void ConfirmChooseSquad(GameObject label)
        {
            int id = int.Parse(label.name.Substring(label.name.LastIndexOf("#") + 1));
            SavedSquad squad = ConfigData.Ships.GetSavedSquads().Where((s) => s.Id == id).First();
            _squadToChoose = squad;
            if (!squad.HasDeadShips)
            {
                ChooseSquad();
            }
            else if (squad.HasAliveShips)
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
            SavedSquad squad = ConfigData.Ships.GetSavedSquads().Where((s) => s.Id == id).First();
            _squadToUnchoose = squad;


            // Change these lines in order to show the confirmation dialogue and then do the action, or to just do the action
            UnchooseSquad();
            //UnchooseSquadConfirmation.Show();
        }
        public void ConfirmStartLevel()
        {
            //Debugger.Log("Starting level!");
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
                case "Yelllow Jacket":
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
            GameObject squadIcon = Instantiate(GetShipIconContainer(shipType));
            squadIcon.transform.SetParent(squadLabel.transform);
            squadIcon.transform.SetAsFirstSibling();
            squadIcon.name = "Icon Container";

            // fill in the squad name 
            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            nameLabelText.text = savedSquad.Name;


            // change the color of the icon
            if (savedSquad.Color != ConfigData.UnsetColor)
            {
                //Debugger.Log($"Setting changable pixels for {savedSquad.Name}");
                UnityEngine.UI.Image squadIconImage = squadIcon.GetComponent<UnityEngine.UI.Image>();

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

            GameObject nameLabel = GameObject.Find($"{squadLabel.name}/Squad Name");
            GameObject squadIcon = GameObject.Find($"{squadLabel.name}/Squad Icon");

            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            string shipType = chosenSquad.GetMostValuableShip().GetFleetShip().Type;


            // change the icon  size
            //Vector2 shipSize = ConfigData.ShipSizes.GetValueOrDefault(shipType);
            //float xToYRatio = (shipSize.x / shipSize.y);
            //float changeInSize = (ConfigData.DragIconSize.y * xToYRatio) - (ConfigData.DragIconSize.x);
            //Vector2 newSize = new Vector2(Mathf.Round(ConfigData.DragIconSize.x + changeInSize), Mathf.Round(ConfigData.DragIconSize.y));

            //// adjust the icon rect
            //squadIcon.GetComponent<RectTransform>().sizeDelta = newSize;
            //// adjust the squad name rect
            //RectTransform nameLabelRectTransform = nameLabel.GetComponent<RectTransform>();
            //nameLabelRectTransform.sizeDelta = new Vector2(ConfigData.OriginalSavedSquadLabelSize.x - changeInSize, ConfigData.OriginalSavedSquadLabelSize.y);


            // fill in the squad name and icon

            nameLabelText.text = chosenSquad.Name;
            UnityEngine.UI.Image squadIconImage = squadIcon.GetComponent<UnityEngine.UI.Image>();
            squadIconImage.sprite = _spriteTypes.GetValueOrDefault(shipType);
            //squadIconImage.SetNativeSize();
            //squadIconImage.transform.localScale = new Vector3(.1f, .1f, 0);

            // change the color of the icon
            if (chosenSquad.Color != ConfigData.UnsetColor)
            {
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(shipType), squadIconImage.sprite);
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
            GameObject nameLabel = GameObject.Find($"{instance.name}/Squad Name");
            GameObject squadIcon = GameObject.Find($"{instance.name}/Squad Icon");

            TMP_Text nameLabelText = nameLabel.GetComponent<TMP_Text>();
            string shipType = savedSquad.GetMostValuableShip().GetFleetShip().Type;


            // change the icon  size
            //Vector2 shipSize = ConfigData.ShipSizes.GetValueOrDefault(shipType);
            //float xToYRatio = (shipSize.x / shipSize.y);
            //float changeInSize = (ConfigData.DragIconSize.y * xToYRatio) - (ConfigData.DragIconSize.x);
            //Vector2 newSize = new Vector2(Mathf.Round(ConfigData.DragIconSize.x + changeInSize), Mathf.Round(ConfigData.DragIconSize.y));

            //// adjust the icon rect
            //squadIcon.GetComponent<RectTransform>().sizeDelta = newSize;
            //// adjust the squad name rect
            //RectTransform nameLabelRectTransform = nameLabel.GetComponent<RectTransform>();
            //nameLabelRectTransform.sizeDelta = new Vector2(ConfigData.OriginalSavedSquadLabelSize.x - changeInSize, ConfigData.OriginalSavedSquadLabelSize.y);


            // fill in the squad name and icon
            UnityEngine.UI.Image squadIconImage = squadIcon.GetComponent<UnityEngine.UI.Image>();

            nameLabelText.text = savedSquad.Name;
            nameLabel.transform.parent.name = $"Saved Squad - {savedSquad.Name} #{savedSquad.Id}";
            squadIconImage.sprite = _spriteTypes.GetValueOrDefault(shipType);
            //squadIconImage.SetNativeSize();
            //squadIconImage.transform.localScale = new Vector3(.1f, .1f, 0);

            // change the color of the icon
            if (savedSquad.Color != ConfigData.UnsetColor)
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
            //Debugger.Log($"Supply capacity {ConfigData.StartingSettings.SupplyCapacity.Count}, {Side}");
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
            shipCountLabel.text = $"{(_currentSquad != null ? _currentSquad.GetShips().Count : 0)} / {ConfigData.Configuration.MaxSquadSize}";
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
            return Regex.Replace(str, @"[^a-zA-Z0-9\-\s!@#%&*_+=:]", "");
            //Debugger.Log($"Unvalidated string: {name}, replaced string {valid}");
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
            SavedSquad savedSquad = ConfigData.Ships.GetSavedSquad(_currentSquad.Id);
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
                savedSquad.GetShips().ForEach((ship) =>
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
                //Debugger.Log($"Saving {_currentUnsavedSquad.Name}");

                //Debugger.Log($"Squad starting position: {_currentUnsavedSquad.StartingPosition}");

                if (ConfigData.Ships.DoesSquadExist(_currentSquad.Id))
                {
                    SaveExistingSquad();
                }
                else
                {
                    SaveNewSquad();
                }

                //Debugger.Log($"Added _currentUnsavedSquad to SavedSquad list");
                //Debugger.Log($"_currentUnsavedSquad: {_currentUnsavedSquad.GetShips().Count}, SavedSquad entry: {_savedSquadsData.GetSquads().Last().GetShips().Count}");


                //Debugger.Log($"Made _currentUnsavedSquad null");
                //Debugger.Log($"_currentUnsavedSquad: {_currentUnsavedSquad}");
                //Debugger.Log($"SavedSquad entry: {_savedSquadsData.GetSquads().Last().GetShips().Count}");

                //Debugger.Log($"JSON : {_currentUnsavedSquad.ToJson()}");
                //ConfigData.WriteJsonFile(_currentUnsavedSquad.ToJson());
            }
        }
        public void ClearChanges()
        {
            //Debugger.Log("Clearing changes");
            int squadId = _currentSquad.Id;
            SavedSquad savedSquad = ConfigData.Ships.GetSavedSquad(squadId);
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
                ConfigData.Ships.RemoveSquad(_currentSquad);

                // remove the entry from the squad ui list
                RemoveSavedSquadFromList(_currentSquad);

                // clear the squad maker
                ClearUnsavedSquad();

                // save the squads
                ConfigData.Ships.SaveSquadData();
            }
        }
        public void SaveNewSquad()
        {
            Debugger.Log("New squad, does not exist yet");
            _currentSquad.Id = ConfigData.GetUserProgressData().GetNextSavedSquadId();
            if (_currentSquad.Name == "")
            {
                _currentSquad.Name = $"Squadron #{_currentSquad.Id}";
            }
            ConfigData.Ships.AddSquad(_currentSquad);
            AddSavedSquadToList(ConfigData.Ships.GetSavedSquads().Last());

            ConfigData.Ships.SaveSquadData();
            ClearUnsavedSquad();
        }
        public void SaveExistingSquad()
        {
            Debugger.Log("Squad does exist, replacing old squad");
            SavedSquad oldSavedSquad = ConfigData.Ships.GetSavedSquad(_currentSquad.Id);

            UpdateSavedSquadInList(GameObject.Find($"Saved Squad - {oldSavedSquad.Name} #{oldSavedSquad.Id}"), _currentSquad);
            List<SavedSquad> savedSquads = ConfigData.Ships.GetSavedSquads();
            int replacementIndex = savedSquads.IndexOf(oldSavedSquad);
            savedSquads[replacementIndex] = (SavedSquad)_currentSquad.Clone();

            ConfigData.Ships.SaveSquadData();
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
            //Debugger.Log($"Loading squad {squad.Name}");

            // set the name text, color and current squad
            _nameText = _squadToLoad.Name;
            _squadColor = _squadToLoad.Color;
            _currentSquad = (SavedSquad)_squadToLoad.Clone();
            SquadNameInput.GetComponent<TMP_InputField>().text = _nameText;

            // close the color picker if active
            CloseColorPicker();
            // make and position all the drag icons
            _currentSquad.GetShips().ForEach((ship) =>
            {
                ship.SetOffset(_currentSquad.StartingPosition + ship.Offset);
                //Debugger.Log($"Set offset for {ship.GetFleetShip().Name}: {ship.Offset}");

            });
            Dropper dropper = GetDropper();
            _currentSquad.GetShips().ForEach((ship) =>
            {
                //Vector2 placementPosition = Utilities.WorldUnitsToScreenPixels(new Vector2(squad.StartingPosition.x + ship.Offset.x, squad.StartingPosition.y + ship.Offset.y), Camera);
                Vector2 placementPosition = Camera.WorldToScreenPoint(ship.Offset);
                //Vector2 placementPosition = Camera.WorldToScreenPoint(ship.Offset);

                //ship.SetOffset(offsetPosition);
                //Debugger.Log($"Starting Position for {ship.GetFleetShip().Name}: {_currentUnsavedSquad.StartingPosition}, Offset position: {ship.Offset}"); 
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
            _squadToLoad = null;


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
                _currentSquad.GetShips().Clear();
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
                FleetShip ship = ConfigData.Ships.GetFleetShip(id);

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
            //Debugger.Log($"Dragging {_currentDragIcon.Icon.name}");
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
                    $"Capacity: {(ship != "Drone" && ship != "Striker" ? ConfigData.Ships.GetShipsOfType(ship).First().GetMaxCapacity().ToString("N0") : "N/A")}";

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
                    SavedSquad squad = ConfigData.Ships.GetSavedSquads().Where((s) => s.Id == id).First();
                    SquadStatBlock stats = squad.Stats;
                    //Debugger.Log($"Squad ID: {id}");

                    TMP_Text titleText = SquadInfoBoxTitle.GetComponent<TMP_Text>();
                    TMP_Text detaislText = SquadInfoBoxDetails.GetComponent<TMP_Text>();


                    titleText.text = $"{squad.Name}";
                    detaislText.text = $"Commander: {stats.Commander}\n\n" +
                        $"Ships: {(squad.GetShips().Count - squad.GetDeadShips().Count).ToString("N0")} / {squad.GetShips().Count.ToString("N0")} " +
                        $"{(squad.HasDeadShips ? $" <color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(ConfigData.GetUIColor("bad"))}><smallcaps><b>(Unfilled)</b></smallcaps></color>" : "")}\n" +
                        $"Capacity: {squad.GetCapacity().ToString("N0")} / {squad.GetMaxCapacity().ToString("N0")}\n" +
                        $"Battles: {stats.BattlesFought.ToString("N0")}: {stats.BattlesWon}W - {stats.BattlesLost}L     (#{ConfigData.Ships.GetSquadRanking(squad, "Record")})\n" +
                        $"Damage Done: {stats.DamageDone.ToString("N0")}     (#{ConfigData.Ships.GetSquadRanking(squad, "DamageDone")})\n" +
                        $"Damage Received: {stats.DamageReceived.ToString("N0")}     (#{ConfigData.Ships.GetSquadRanking(squad, "DamageReceived")})\n" +
                        $"Kills: {stats.Kills.ToString("N0")}     (#{ConfigData.Ships.GetSquadRanking(squad, "Kills")})\n" +
                        $"Ships Lost: {stats.ShipsLost.ToString("N0")}     (#{ConfigData.Ships.GetSquadRanking(squad, "ShipsLost")})\n";

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
                    Debugger.Log($"No selected object: {label}");  
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
                    FleetShip ship = ConfigData.Ships.GetFleetShip(id);
                    //Debugger.Log($"Squad ID: {id}");

                    TMP_Text titleText = ShipStatsBoxTitle.GetComponent<TMP_Text>();
                    TMP_Text detaislText = ShipStatsBoxDetails.GetComponent<TMP_Text>();


                    titleText.text = $"{ship.Name}";
                    detaislText.text = $"Battles: {ship.BattlesFought.ToString("N0")}: {ship.BattlesWon}W - {ship.BattlesLost}L     (#{ConfigData.Ships.GetShipRanking(ship, "Record")})\n" +
                        $"Shots Fired: {ship.ShotsFired.ToString("N0")}     (#{ConfigData.Ships.GetShipRanking(ship, "ShotsFired")})\n" +
                        $"Damage Done: {ship.DamageDone.ToString("N0")}     (#{ConfigData.Ships.GetShipRanking(ship, "DamageDone")})\n" +
                        $"Damage Received: {ship.DamageReceived.ToString("N0")}     (#{ConfigData.Ships.GetShipRanking(ship, "DamageReceived")})\n" +
                        $"Kills: {ship.Kills.ToString("N0")}     (#{ConfigData.Ships.GetShipRanking(ship, "Kills")})\n";


                    ShipStatsBox.SetActive(true);
                    Vector2 mouse = Input.mousePosition;
                    //Vector2 screenPoint = Camera.WorldToScreenPoint(ShipStatsBoxOffset);
                    //Vector2 change = new Vector2(Mathf.Abs(BaseWorldPoint.x - screenPoint.x), Mathf.Abs(BaseWorldPoint.y - screenPoint.y));

                    Vector2 change = Utilities.WorldUnitsToScreenPixels(ShipStatsBoxOffset, Camera);
                    //Vector2 change = ShipStatsBoxOffset;


                    //Debugger.Log($"mouse: {mouse}, change: {change}");
                    ShipStatsBox.transform.position = new Vector2(mouse.x + change.x, mouse.y + change.y);
                }
                else
                {
                    Debugger.Log($"No selected object: {label}");
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


                //Debugger.Log($"mouse: {mouse}, change: {change}");
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
        public void OpenColorPicker()
        {
            //Debugger.Log("Opening/Closing color picker");
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
            //Debugger.Log("Trying to pick color");
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
                Debugger.Log("Already starting the level!");
                return;
            }
            _startingLevel = true;
            Invoke(nameof(ProcessStartingLevel), .1f);



        }
        private void ProcessStartingLevel()
        {
            //Debugger.Log("On to the level!");

            // add the sqauds
            _chosenSquads.ForEach((chosenSquad) =>
            {
                ConfigData.SquadsChosenForLevel.Add((SavedSquad)chosenSquad.Clone());
            });

            //Debugger.Log($"SMS: {ConfigData.SquadMakerSide}, SMFS: {ConfigData.Configuration.SquadMakerFirstSide}, SMSS: {ConfigData.Configuration.SquadMakerSecondSide}");
            // go to next side if you need to
            List<SavedSquad> newlySavedOpposingSquads = new List<SavedSquad>();
            if (Side == ConfigData.Configuration.SquadMakerFirstSide)
            {
                if (_chosenOpposingForceOption == 0) // [alert] order needs to be changed
                {
                    ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerSecondSide;
                    _nextScene = "Squad Maker";
                    Invoke(nameof(LoadScene), .25f);
                    return;
                }
                else if (_chosenOpposingForceOption == 1)
                {
                    _chosenSquads.ForEach((savedSquad) =>
                    {
                        SavedSquad newSquad = new AutoBuiltSquad(ConfigData.Configuration.SquadMakerSecondSide, "random", savedSquad, false, false).Squad;
                        newlySavedOpposingSquads.Add(newSquad);
                    });
                }
                else if (_chosenOpposingForceOption == 2)
                {
                    _chosenSquads.ForEach((savedSquad) =>
                    {
                        SavedSquad newSquad = new AutoBuiltSquad(ConfigData.Configuration.SquadMakerSecondSide, "random", savedSquad, true, false).Squad;
                        newlySavedOpposingSquads.Add(newSquad);
                    });

                }
                else if (_chosenOpposingForceOption == 3)
                {
                    _chosenSquads.ForEach((savedSquad) =>
                    {
                        SavedSquad newSquad = new AutoBuiltSquad(ConfigData.Configuration.SquadMakerSecondSide, "random", savedSquad, false, true).Squad;
                        newlySavedOpposingSquads.Add(newSquad);
                    });
                }
            }
            newlySavedOpposingSquads.ForEach((squad) =>
            {
                Debugger.Log($"Made squad worth {squad.GetMaxTsv()} tsv.");
                string ships = "";
                squad.GetShips().ForEach((s) => ships += $"{s.ShipType}, ");
                Debugger.Log(ships);
            });
            ConfigData.SquadsChosenForLevel.AddRange(newlySavedOpposingSquads);
            //ConfigData.SquadsChosenForLevel.ForEach((s) => Debugger.Log(s.ToString()));
            _nextScene = "Training Room Test II";
            Invoke(nameof(LoadScene), .5f);
            //SceneManager.LoadSceneAsync("Training Room One Screen", LoadSceneMode.Single); // [alert] this should go to the actual level based on the level number
            //SceneManager.LoadSceneAsync("RL Tiny Box", LoadSceneMode.Single); // [alert] [rl-training]
        }
        private void LoadScene()
        {
            Debugger.Log("Loading scene!");
            SceneManager.LoadSceneAsync(_nextScene, LoadSceneMode.Single);
        }
        public void ChangeOpposingForceDropdown(int option)
        {
            TMP_Dropdown dropdown = OpposingForcePresetDropdown.GetComponentInChildren<TMP_Dropdown>();
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

            //Debugger.Log($"User chose {dropdown.options[option].text}, {_chosenOpposingForceOption}");
        }





        private void OnDestroy()
        {
            //Debugger.Log("Destroying squad maker scene");
        }
    }
}