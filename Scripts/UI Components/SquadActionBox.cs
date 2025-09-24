


using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    public class SquadActionBox : MonoBehaviour
    {
        public GameObject MatchSpeedButton, CeaseFireButton, AttackOnSightButton, PatrolButton, GuardButton, ChaseButton, HoldButton, DetonateButton, ChargeButton, LockOnButton, 
            DropBeaconButton, TypeSelector, ActionTitle, ActionExplanation;
        public TMP_Text ActionExplanationText;
        private EventSystem _eventSystem;
        private SquadMaker _squadMaker = null;
        private Level Level = null;
        private string _blankShipType = "——————";
        private bool _autoSetDropdownValue = false;
        private int Side;


        private bool HasSquadMaker => _squadMaker != null;
        private bool HasLevel => Level != null;    

        public void Setup(SquadMaker squadMaker, EventSystem eventSystem, int side)
        {
            _squadMaker = squadMaker;
            _eventSystem = eventSystem;
            Side = side;
            Destroy(PatrolButton);
            Destroy(GuardButton);
            Destroy(HoldButton);
            Destroy(DetonateButton);
            Destroy(ChargeButton);
            Destroy(LockOnButton);
            Destroy(DropBeaconButton);
            SetDropdownOptions();
            
        }
        public void Setup(Scene scene, Level level, EventSystem eventSystem, int side)
        {
            Level = level;
            _eventSystem = eventSystem;
            Side = side;
            SetDropdownOptions();
            ActionExplanationText.fontSize = 14;
        }
        public void Hide()
        {
            gameObject.SetActive(false);
        }
        public void SetupForSquad()
        {
            gameObject.SetActive(true);
            SetSquadsText();
            HighlightSelectedButtons();
            SetDropdownOptions();
            SetDropdownValue();
        }
        public void SetSquadsText()
        {
            TMP_Text squadText = GameObject.Find("Squad Text").GetComponentInChildren<TMP_Text>();
            if (squadText != null)
            {
                if (HasLevel)
                {
                    int sum = Level.State.SelectedSquads.Sum((squad) => squad.GetShips().Count);
                    string shipCount = $"({sum}) ships";
                    if (sum == 1)
                    {
                        shipCount = $"({sum} ship)";
                    }
                    squadText.text = $"{shipCount} - {GetSquadNames()}";
                }
                else
                {
                    squadText.text = GetSquadNames();
                }
                
            }
        }
        public string GetSquadNames()
        {
            string squadNames = "";
            if (HasSquadMaker)
            {
                SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                if (currentSquad != null)
                {
                    squadNames += _squadMaker.GetSquadName();
                }
            }
            else if (HasLevel)
            {
                List<Squad> selectedSquads = Level.State.GetSelectedSquads();
                selectedSquads.ForEach(squad =>
                {
                    squadNames += $"{squad.Name}, ";
                });
                squadNames = squadNames.Remove(squadNames.Length - 2);
            }
            return squadNames;
        }
        private void SetDropdownOptions()
        {
            TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
            dropdown.options.Clear();
            dropdown.options.Add(new TMP_Dropdown.OptionData(_blankShipType));
            ShipTypes().ToList().ForEach(ship =>
            {
                //Debug.Log("Setting drop down option");

                dropdown.options.Add(new TMP_Dropdown.OptionData(Utilities.ConvertShipTypeToPluralName[ship]));
            });
        }
        private List<ConfigData.ShipTypes> ShipTypes()
        {

            if (Side == ConfigData.Configuration.BeeSide)
            {
                return ConfigData.UserProgressData.VisibleHumanShipTypes.ToList();
            }
            return ConfigData.UserProgressData.VisibleBeeShipTypes.ToList();
        }
        private void SetDropdownValue()
        {
            
            ConfigData.ShootingStrategyTypes shootingStrategy = GetShootingStrategy();
            TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
            _autoSetDropdownValue = true;

            if ((int) shootingStrategy > 15)
            {
                string shipName = Utilities.ConvertShipTypeToPluralName[Utilities.ConvertShootingStrategyToShipType[shootingStrategy]];
                //Debug.Log($"Ship name is {shipName}");
                dropdown.value = dropdown.options.FindIndex(option => option.text == shipName);
            }
            else
            {
                dropdown.value = 0;
            }
            _autoSetDropdownValue = false;

        }
        private ConfigData.ShootingStrategyTypes GetShootingStrategy()
        {
            if (HasSquadMaker)
            {
                SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                if (currentSquad != null)
                {
                    return currentSquad.ChosenShootingStrategy;
                }
            }
            else if (HasLevel)
            {
                List<Squad> selectedSquads = Level.State.GetSelectedSquads();
                if (selectedSquads.Count > 0)
                {
                    ConfigData.ShootingStrategyTypes shootingStrategy = selectedSquads.First().GetShootingStrategy();
                    //Debug.Log($"Shooting strategy is {shootingStrategy}");
                    if (selectedSquads.All(s => s.GetShootingStrategy() == shootingStrategy))
                    {
                        return shootingStrategy;
                    }
                }

            }
            return 0;
        }
        public void HighlightSelectedButtons()
        {

            if (IsAction(ConfigData.SquadActions.IsMatchingSpeed))
            {
                HighlightButton(MatchSpeedButton);
            }
            else
            {
                ResetButton(MatchSpeedButton);
            }
            if (IsAction(ConfigData.SquadActions.CeaseFire))
            {
                HighlightButton(CeaseFireButton);
            }
            else
            {
                ResetButton(CeaseFireButton);
            }
            if (IsAction(ConfigData.SquadActions.AttackOnSight))
            {
                HighlightButton(AttackOnSightButton);
            }
            else
            {
                ResetButton(AttackOnSightButton);
            }
            if (IsAction(ConfigData.SquadActions.Chase))
            {
                HighlightButton(ChaseButton);
            }
            else
            {
                ResetButton(ChaseButton);
            }
            if (HasLevel)
            {
                ChargeButton.SetActive(Level.State.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == ConfigData.ShipTypes.Barge)));
                DetonateButton.SetActive(Level.State.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == ConfigData.ShipTypes.FireBarge)));
                DropBeaconButton.SetActive(Level.State.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == ConfigData.ShipTypes.Scout && ((Scout)ship).CanDropBeacons)));

                if (IsAction(ConfigData.SquadActions.Patrol))
                {
                    HighlightButton(PatrolButton);
                }
                else
                {
                    //Debug.Log($"Not patrolling: {Level.State.GetSelectedSquads().First().GetCommandStrategy()});
                    ResetButton(PatrolButton);
                }
                if (IsAction(ConfigData.SquadActions.Guard))
                {
                    HighlightButton(GuardButton);
                }
                else
                {
                    ResetButton(GuardButton);
                }
                if (IsAction(ConfigData.SquadActions.Hold))
                {
                    HighlightButton(HoldButton);
                }
                else
                {
                    ResetButton(HoldButton);
                }
                if (IsAction(ConfigData.SquadActions.LockOn))
                {
                    HighlightButton(LockOnButton);
                }
                else
                {
                    ResetButton(LockOnButton);
                }
            }

            ConfigData.ShootingStrategyTypes shootingStrategy = GetShootingStrategy();
            //Debug.Log($"Squad(s) shooting strategy: {shootingStrategy}");
            ConfigData.TypesOfShootingStrategies.Where(s => (int) s <= 15).ToList().ForEach(s =>
            {
                //Debug.Log($"{Utilities.ConvertShootingStrategyTypeToName[s]} Button");
                GameObject buttonLabel = GameObject.Find($"{Utilities.ConvertShootingStrategyTypeToName[s]} Button"); // [effeciency] could be made better by having a dictionary of the buttons
                if (shootingStrategy == s)
                {
                    HighlightButton(buttonLabel);
                }
                else
                {
                    ResetButton(buttonLabel);
                }
            });

            if ((int) shootingStrategy > 15)
            {
                HighlightButton(TypeSelector);
            }
            else
            {
                ResetButton(TypeSelector);
            }
        }

        private bool IsAction(ConfigData.SquadActions action)
        {
            if (HasSquadMaker)
            {
                SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                if (currentSquad != null)
                {
                    switch(action)
                    {
                        case ConfigData.SquadActions.IsMatchingSpeed:
                            return currentSquad.IsMatchingSpeed;
                        case ConfigData.SquadActions.CeaseFire:
                            return currentSquad.CeaseFire;
                        case ConfigData.SquadActions.AttackOnSight:
                            return !currentSquad.CeaseFire;
                        case ConfigData.SquadActions.Chase:
                            return currentSquad.IsSetToChase;

                    }
                }
            }
            else if (HasLevel)
            {
                List<Squad> selectedSquads = Level.State.GetSelectedSquads();
                
                switch (action)
                {
                    case ConfigData.SquadActions.IsMatchingSpeed:
                        return selectedSquads.All((s) => s.IsMatchingSpeed);
                    case ConfigData.SquadActions.CeaseFire:
                        return selectedSquads.All((s) => s.CeaseFire);
                    case ConfigData.SquadActions.AttackOnSight:
                        return selectedSquads.All((s) => s.AttackOnSight);
                    case ConfigData.SquadActions.Patrol:
                        return selectedSquads.All((s) => s.GetCommandStrategy() == ConfigData.CommandTypes.Patrol);
                    case ConfigData.SquadActions.Guard:
                        return selectedSquads.All((s) => s.GetCommandStrategy() == ConfigData.CommandTypes.Guard);
                    case ConfigData.SquadActions.Chase:
                        return selectedSquads.All((s) => s.ShouldChase());
                    case ConfigData.SquadActions.Hold:
                        return selectedSquads.All((s) => s.Holding);
                    case ConfigData.SquadActions.LockOn:
                        return selectedSquads.All((s) => s.IsLockedOn);
                }
            }
            return false;
        }

        private void HighlightButton(GameObject buttonLabel)
        {
            //Debug.Log($"Highlighting {buttonLabel.name}");
            Color highlightColor = ConfigData.GetUIColor("action-button-highlight");
            if (buttonLabel.name.StartsWith("Type "))
            {
                TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
                ColorBlock colorBlock = dropdown.colors;
                colorBlock.selectedColor = highlightColor;
                colorBlock.normalColor = highlightColor;
                dropdown.colors = colorBlock;

            }
            else
            {
                Button button = GetButton(buttonLabel);
                if (button != null)
                {
                    ColorBlock colorBlock = button.colors;
                    colorBlock.normalColor = highlightColor;
                    button.colors = colorBlock;
                }
            }

        }
        private void DeselectButton()
        {
            _eventSystem.SetSelectedGameObject(null);
        }
        private Button GetButton(GameObject buttonLabel)
        {
            if (buttonLabel != null)
            {
                Button buttonComponent = buttonLabel.GetComponent<Button>();

                return buttonComponent;
            }
            return null;
        }
        private void ResetButton(GameObject buttonLabel)
        {
            //Debug.Log($"Resetting {buttonLabel}");
            Button button = GetButton(buttonLabel);
            Color normalColor = ConfigData.GetUIColor("action-button-normal");

            if (buttonLabel.name.StartsWith("Type "))
            {
                TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();

                ColorBlock colorBlock = dropdown.colors;
                colorBlock.selectedColor = normalColor;
                colorBlock.normalColor = normalColor;
                dropdown.colors = colorBlock;
            }
            else if (button != null)
            {
                ColorBlock colorBlock = button.colors;
                colorBlock.normalColor = normalColor;
                button.colors = colorBlock;
            }
        }
        public void SetExplanationText(string button = "")
        {
            if (HasSquad())
            {
                if (button == "")
                {
                    TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
                    button = dropdown.options[dropdown.value].text;
                }
                //Debug.Log($"Setting text for {button}");
                TMP_Text title = ActionTitle.GetComponentInChildren<TMP_Text>();
                
                title.text = button;

                string side = "Bees";
                if (Side == ConfigData.Configuration.BeeSide)
                {
                    side = "Human ships";
                }
                string beginningActionText = "The selected squad(s) will";
                string beginningStrategyText = $"When there are multiple {side} within range, the ships of the selected squad(s) will prioritize shooting at the";

                if (HasSquadMaker)
                {
                    beginningActionText = "This squad will";
                    beginningStrategyText = $"When there are multiple {side} within range, the ships of this squad will prioritize shooting at the";
                }
                switch (button)
                {
                    case "Patrol":
                        ActionExplanationText.text = $"{beginningActionText} patrol around the border that you select (by selecting an area), engaging any {side} they encounter.";
                        break;

                    case "Guard":
                        ActionExplanationText.text = $"{beginningActionText} guard the squad you select (by right clicking on it) by flying nearby and following it.";
                        break;

                    case "Chase":
                        ActionExplanationText.text = $"{beginningActionText} chase down and engage the first {side} they see.";
                        break;

                    case "Hold":
                        ActionExplanationText.text = $"{beginningActionText} hold their position and fire upon any {side} that gets within range.";
                        break;

                    case "Detonate":
                        ActionExplanationText.text = $"The Fire Barge(s) of the selected squad(s) will detonate their nuclear cargo, severely damaging or destroying all ships around them.";
                        break;

                    case "Charge":
                        ActionExplanationText.text = $"The Barge(s) of the selected squad(s) will build up power and then charge forward, ramming ships in front of them and taking damage.";
                        break;

                    case "Drop Beacon":
                        ActionExplanationText.text = $"The Scouts(s) of the selected squad(s) will drop a beacon, clearing away the fog of war in a small area until destroyed.";
                        break;

                    case "Match Speed":
                        ActionExplanationText.text = $"{beginningActionText} all fly at the same speed: the speed of the slowest ships.";
                        break;

                    case "Attack on Sight":
                        ActionExplanationText.text = $"{beginningActionText} fire upon any {side} that gets within range. This is standard behavior.";
                        break;

                    case "Cease Fire":
                        ActionExplanationText.text = $"{beginningActionText} not fire on anyone under any circumstances.";
                        break;
                    case "Lock On":
                        ActionExplanationText.text = $"{beginningActionText} chase and attack the currently targeted squad until they or it are destroyed. They'll ignore all other commands until this is canceled.";
                        break;



                    case "First Seen":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} they see first.";
                        break;
                    case "Random":
                        ActionExplanationText.text = $"The ships of the selected squad(s) will shoot randomly at any {side} they see.";
                        if (HasSquadMaker)
                        {
                            ActionExplanationText.text = $"The ships of this squad will shoot randomly at any {side} they see.";
                        }
                        break;

                    case "Revenge":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have most recently killed our own ships.";
                        break;

                    case "Most Dangerous":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have dealt the most damage.";
                        break;

                    case "Most Health":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the most health.";
                        break;

                    case "Least Health":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the least health.";
                        break;

                    case "Most Powerful":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the most fire power.";
                        break;

                    case "Least Powerful":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the least fire power.";
                        break;

                    case "Closest":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that are closest to them.";
                        break;

                    case "Furthest":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that are furthest from them.";
                        break;

                    case "Most Range":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the longest range.";
                        break;

                    case "Least Range":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the shortest range.";
                        break;

                    case "Fastest":
                        ActionExplanationText.text = $"{beginningStrategyText} fastest {side}.";
                        break;

                    case "Slowest":
                        ActionExplanationText.text = $"{beginningStrategyText} slowest {side}.";
                        break;

                    case "Most Valuable":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the most estimated strategic value.";
                        break;

                    case "Least Valuable":
                        ActionExplanationText.text = $"{beginningStrategyText} {side} that have the least estimated strategic value.";
                        break;

                    case "Barges":
                    case "Beacons":
                    case "Carriers":
                    case "Cruisers":
                    case "Dreadnoughts":
                    case "Drones":
                    case "Factories":
                    case "Fire Barges":
                    case "Flagships":
                    case "Frigates":
                    case "Gunships":
                    case "Scouts":
                    case "Strikers":
                    case "Warp Gates":
                    case "Beehives":
                    case "Bumblebees":
                    case "Carpenter Bees":
                    case "Honeybees":
                    case "Hornets":
                    case "Leafcutters":
                    case "Queen":
                    case "Wasps":
                    case "Yellow Jackets":
                        ActionExplanationText.text = $"{beginningStrategyText} {button}.";
                        break;
                }
            }
           


        }
        public bool HasSquad()
        {
            if (HasSquadMaker)
            {
                SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                if (currentSquad != null)
                {
                    return true;
                }
            }
            else if (HasLevel)
            {
                return Level.State.HasSelectedSquads;
            }
            return false;
        }
        public void ClearExplanationText()
        {
            TMP_Text title = ActionTitle.GetComponentInChildren<TMP_Text>();
            TMP_Text explanation = ActionExplanation.GetComponentInChildren<TMP_Text>();
            title.text = "";
            explanation.text = "";
        }
        public void SetAction(string action)
        {
            UIAudioController.Instance.PlayButtonSound();
            if (HasSquad())
            {
                //Debug.Log($"Setting the squads to {action}!");
                if (HasSquadMaker)
                {
                    SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                    currentSquad.SetChanged(true);

                    switch (action)
                    {
                        case "Match Speed":
                            currentSquad.IsMatchingSpeed = !currentSquad.IsMatchingSpeed;
                            break;

                        case "Attack on Sight":
                            currentSquad.CeaseFire = false;
                            break;

                        case "Cease Fire":
                            currentSquad.CeaseFire = true;
                            break;
                        case "Chase":
                            currentSquad.IsSetToChase = !currentSquad.IsSetToChase;
                            break;
                    }

                }
                else if (HasLevel)
                {
                    switch (action)
                    {
                        case "Patrol":
                            Patrol();
                            break;

                        case "Guard":
                            Guard();
                            break;

                        case "Chase":
                            Chase();
                            break;

                        case "Hold":
                            Hold();
                            break;

                        case "Detonate":
                            Detonate();
                            return;

                        case "Charge":
                            Charge();
                            break;

                        case "Drop Beacon":
                            DropBeacon();
                            break;

                        case "Match Speed":
                            MatchSpeed();
                            break;

                        case "Attack on Sight":
                            AttackOnSight();
                            break;

                        case "Cease Fire":
                            CeaseFire();
                            break;

                        case "Lock On":
                            LockOn();
                            break;
                    }
                }
                DeselectButton();
                HighlightSelectedButtons();
            }
                
        }
        public void DropBeacon()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    foreach (Ship ship in squad.GetShips().Where((s) => s.ShipType == ConfigData.ShipTypes.Scout))
                    {
                        ((Scout)ship).DropBeacon();
                    }
                });
            }

        }
        public void Charge()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    foreach (Ship ship in squad.GetShips().Where((s) => s.ShipType == ConfigData.ShipTypes.Barge))
                    {
                        if (!ship.CannotChangeMovementOrders)
                        {
                            StartCoroutine(((Barge)ship).ChargeForward());
                        }
                        else
                        {
                            ((Barge)ship).WaitingForNewCharge = true;
                        }
                    }
                });
            }

        }
        public void Detonate()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    squad.GetShips().Where((s) => s.ShipType == ConfigData.ShipTypes.FireBarge).ToList().ForEach((ship) =>
                    {
                        Level.Stage.Audio.BargeDetonationClick.Play();
                        ((FireBarge)ship).Detonate();
                    });
                });
            }

        }
        public void Hold()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    squad.StopChasing();
                });
                HighlightSelectedButtons();
            }

        }
        public void Chase()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    squad.SetChase(true);
                });
                HighlightSelectedButtons();
            }

        }
        public void Guard()
        {
            if (HasSquad())
            {
                Level.Stage.InputManager.SetSelectGuardTargetActive();
                HighlightSelectedButtons();
            }

        }
        public void Patrol()
        {
            if (HasSquad())
            {
                Level.Stage.InputManager.SetPatrolAreaActive();
                HighlightSelectedButtons();
            }

        }
        public void CeaseFire()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    squad.SetSquadCeaseFire(true);
                });
                HighlightSelectedButtons();
            }

        }
        public void AttackOnSight()
        {
            if (HasSquad())
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    if (!squad.HasCommand || squad.GetCommand().CommandType != ConfigData.CommandTypes.Heal)
                    {
                        squad.SetSquadCeaseFire(false);
                    }
                });
                HighlightSelectedButtons();
            }

        }
        public void LockOn()
        {
            if (HasSquad())
            {
                bool onOrOff = !Level.State.GetSelectedSquads().All((squad) => squad.IsLockedOn);
                Debug.Log($"onOrOff: {onOrOff}");
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    //Debug.Log(squad);
                    //Debug.Log(squad.HasCommand);
                    //Debug.Log(squad.GetCommand()?.HasEnemy);
                    if (squad.HasCommand && squad.GetCommand().HasEnemy)
                    {
                        //Debug.Log($"Setting is locked on: {onOrOff}");
                        squad.IsLockedOn = onOrOff;
                        //squad.GetShips().ForEach((s) =>  s.CannotChangeMovementOrders = onOrOff);
                    }
                });
                HighlightSelectedButtons();
            }

        }
        public void MatchSpeed()
        {
            if (HasSquad())
            {
                List<Squad> squads = Level.State.GetSelectedSquads();
                bool IsMatchingSpeed = squads.All(squad => squad.IsMatchingSpeed);
                float slowestSpeed = squads.Min(squad => squad.SlowestSpeed);
                squads.ForEach((squad) =>
                {
                    if (IsMatchingSpeed)
                    {
                        squad.UnmatchSpeed();
                    }
                    else
                    {
                        squad.MatchSpeed(slowestSpeed);
                    }
                });
                HighlightSelectedButtons();
            }

        }
        public void SetShootingStrategy(string strategy)
        {
            //Debug.Log("Set shooting strategy");
            UIAudioController.Instance.PlayButtonSound();
            if (HasSquad())
            {
                
                ShipTypes().ToList().ForEach((shipType) =>
                {
                    if (strategy == Utilities.ConvertShipTypeToPluralName[shipType])
                    {
                        strategy = $"Type {Utilities.ConvertShipTypeToShipTypeLetter[shipType]}";
                    }
                });

                ConfigData.ShootingStrategyTypes shootingStrategy = Utilities.ConvertShootingStrategyNameToType[strategy];


                if (HasSquadMaker)
                {
                    SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                    currentSquad.SetChanged(true);
                    currentSquad.ChosenShootingStrategy = shootingStrategy;
                }
                else if (HasLevel)
                {
                    Level.State.GetSelectedSquads().ForEach((squad) =>
                    {
                        //Debug.Log($"Setting the squad to shoot with {strategy}!");
                        squad.SetShootingStrategy(shootingStrategy);
                    });
                }
                SetDropdownValue();
                DeselectButton();
                HighlightSelectedButtons();
            }
            //else
            //{
            //    Debug.Log("No squads to set shooting strategy");
            //}

        }
        public void SetTypeStrategy(int strategy)
        {
            UIAudioController.Instance.PlayButtonSound();
            if (!_autoSetDropdownValue)
            {
                //Debug.Log($"Set type strategy {strategy}");
                TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
                if (strategy < dropdown.options.Count)
                {
                    string shipName = dropdown.options[strategy].text;

                    if (strategy == 0) // "-------" No option chosen
                    {
                        shipName = Utilities.ConvertShootingStrategyTypeToName[ConfigData.DefaultShootingStrategy];
                    }
                    //Debug.Log($"setting shooting strategy to {shipName}");
                    SetShootingStrategy(shipName);
                }

            }
            else
            {
                _autoSetDropdownValue = false;
            }


        }
    }
}