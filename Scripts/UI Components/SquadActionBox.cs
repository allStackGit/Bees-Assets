


using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
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
        public GameObject MatchSpeedButton, CeaseFireButton, AttackOnSightButton, PatrolButton, GuardButton, ChaseButton, HoldButton, DetonateButton, ChargeButton, 
            DropBeaconButton, TypeSelector, ActionTitle, ActionExplanation;

        private EventSystem _eventSystem;
        private SquadMaker _squadMaker = null;
        private LevelStage Level = null;
        private string _blankShipType = "———————";
        private bool _autoSetDropdownValue = false;
        private int Side;


        private bool HasSquadMaker => _squadMaker != null;
        private bool HasLevel => Level != null;    

        public void Setup(SquadMaker squadMaker, EventSystem eventSystem, int side)
        {
            this._squadMaker = squadMaker;
            _eventSystem = eventSystem;
            Side = side;
            Destroy(PatrolButton);
            Destroy(GuardButton);
            Destroy(HoldButton);
            Destroy(DetonateButton);
            Destroy(ChargeButton);
            Destroy(DropBeaconButton);
            ActualSetup();
            
        }
        public void Setup(LevelStage level, EventSystem eventSystem, int side)
        {
            this.Level = level;
            _eventSystem = eventSystem;
            Side = side;
            ActualSetup();

        }
        public void Hide()
        {
            gameObject.SetActive(false);
        }
        private void ActualSetup()
        {
            SetDropdownOptions();
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
                    int sum = Level.GetState().SelectedSquads.Sum((squad) => squad.GetShips().Count);
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
                List<Squad> selectedSquads = Level.GetState().GetSelectedSquads();
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
                if (ship == "Queen")
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData($"{ship}"));
                }else if (ship == "Factory")
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData($"Factories"));
                }
                else
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData($"{ship}s"));
                }
            });
        }
        private List<string> ShipTypes()
        {

            if (Side == ConfigData.Configuration.BeeSide)
            {
                return ConfigData.Configuration.VisibleHumanShipTypes.ToList();
            }
            return ConfigData.Configuration.VisibleBeeShipTypes.ToList();
        }
        private void SetDropdownValue()
        {
            
            string shootingStrategy = GetShootingStrategy();
            TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
            _autoSetDropdownValue = true;

            if (shootingStrategy.StartsWith("Type "))
            {
                string shipName = Utilities.ConvertShipTypeToPluralName(shootingStrategy);
                //Debug.Log($"Ship name is {shipName}");
                dropdown.value = dropdown.options.FindIndex(option => option.text == shipName);
            }
            else
            {
                dropdown.value = 0;
            }
            _autoSetDropdownValue = false;

        }
        private string GetShootingStrategy()
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
                List<Squad> selectedSquads = Level.GetState().GetSelectedSquads();
                if (selectedSquads.Count > 0)
                {
                    string shootingStrategy = selectedSquads.First().GetShootingStrategy();
                    //Debug.Log($"Shooting strategy is {shootingStrategy}");
                    if (selectedSquads.All(s => s.GetShootingStrategy() == shootingStrategy))
                    {
                        return shootingStrategy;
                    }
                }

            }
            return "";
        }
        public void HighlightSelectedButtons()
        {

            if (IsAction("IsMatchingSpeed"))
            {
                HighlightButton(MatchSpeedButton);
            }
            else
            {
                ResetButton(MatchSpeedButton);
            }
            if (IsAction("CeaseFire"))
            {
                HighlightButton(CeaseFireButton);
            }
            else
            {
                ResetButton(CeaseFireButton);
            }
            if (IsAction("Attack on Sight"))
            {
                HighlightButton(AttackOnSightButton);
            }
            else
            {
                ResetButton(AttackOnSightButton);
            }
            if (IsAction("Chase"))
            {
                HighlightButton(ChaseButton);
            }
            else
            {
                ResetButton(ChaseButton);
            }
            if (HasLevel)
            {
                GameState state = Level.GetState();
                ChargeButton.SetActive(state.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == "Barge")));
                DetonateButton.SetActive(state.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == "Fire Ship")));
                DropBeaconButton.SetActive(state.GetSelectedSquads().Any((squad) => squad.GetShips().Any((ship) => ship.ShipType == "Scout")));

                if (IsAction("Patrol"))
                {
                    HighlightButton(PatrolButton);
                }
                else
                {
                    //Debug.Log($"Not patrolling: {Level.GetState().GetSelectedSquads().First().GetCommandStrategy()}");
                    ResetButton(PatrolButton);
                }
                if (IsAction("Guard"))
                {
                    HighlightButton(GuardButton);
                }
                else
                {
                    ResetButton(GuardButton);
                }
                if (IsAction("Hold"))
                {
                    HighlightButton(HoldButton);
                }
                else
                {
                    ResetButton(HoldButton);
                }
            }

            string shootingStrategy = GetShootingStrategy();
            //Debug.Log($"Squad(s) shooting strategy: {shootingStrategy}");
            ConfigData.Configuration.ShootingStrategies.Where(s => !s.StartsWith("Type ")).ToList().ForEach(s =>
            {
                //Debug.Log($"{s} Button");
                GameObject buttonLabel = GameObject.Find($"{s} Button"); // [effeciency] could be made better by having a dictionary of the buttons
                if (shootingStrategy == s)
                {
                    HighlightButton(buttonLabel);
                }
                else
                {
                    ResetButton(buttonLabel);
                }
            });

            if (shootingStrategy.StartsWith("Type "))
            {
                HighlightButton(TypeSelector);
            }
            else
            {
                ResetButton(TypeSelector);
            }
        }

        private bool IsAction(string action)
        {
            if (HasSquadMaker)
            {
                SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                if (currentSquad != null)
                {
                    switch(action)
                    {
                        case "IsMatchingSpeed":
                            return currentSquad.IsMatchingSpeed;
                        case "CeaseFire":
                            return currentSquad.CeaseFire;
                        case "Attack on Sight":
                            return !currentSquad.CeaseFire;
                        case "Chase":
                            return currentSquad.IsSetToChase;
                    }
                }
            }
            else if (HasLevel)
            {
                List<Squad> selectedSquads = Level.GetState().GetSelectedSquads();
                
                switch (action)
                {
                    case "IsMatchingSpeed":
                        return selectedSquads.All((s) => s.IsMatchingSpeed);
                    case "CeaseFire":
                        return selectedSquads.All((s) => s.CeaseFire);
                    case "Attack on Sight":
                        return selectedSquads.All((s) => s.AttackOnSight);
                    case "Patrol":
                        return selectedSquads.All((s) => s.GetCommandStrategy() == "Patrol");
                    case "Guard":
                        return selectedSquads.All((s) => s.GetCommandStrategy() == "Guard");
                    case "Chase":
                        return selectedSquads.All((s) => s.ShouldChase());
                    case "Hold":
                        return selectedSquads.All((s) => s.Holding);
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
                TMP_Text explanation = ActionExplanation.GetComponentInChildren<TMP_Text>();
                title.text = button;

                string side = "Bees";
                if (Side == ConfigData.Configuration.BeeSide)
                {
                    side = "Human ships";
                }
                string beginningActionText = "The selected squadron(s) will";
                string beginningStrategyText = $"When there are multiple {side} within range, the ships of the selected squadron(s) will prioritize shooting at the";

                if (HasSquadMaker)
                {
                    beginningActionText = "This squadron will";
                    beginningStrategyText = $"When there are multiple {side} within range, the ships of this squadron will prioritize shooting at the";
                }
                switch (button)
                {
                    case "Patrol":
                        explanation.text = $"{beginningActionText} patrol around the border that you select (by selecting an area), engaging any {side} they encounter.";
                        break;

                    case "Guard":
                        explanation.text = $"{beginningActionText} guard the squadron you select (by right clicking on it) by flying nearby and following it.";
                        break;

                    case "Chase":
                        explanation.text = $"{beginningActionText} chase down and engage the first {side} they see.";
                        break;

                    case "Hold":
                        explanation.text = $"{beginningActionText} hold their position and fire upon any {side} that gets within range.";
                        break;

                    case "Detonate":
                        explanation.text = $"The Fire Ship(s) of the selected squadron(s) will detonate their nuclear cargo, severely damaging or destroying all ships around them.";
                        break;

                    case "Charge":
                        explanation.text = $"The Barge(s) of the selected squadron(s) will build up power and then charge forward, ramming ships in front of them and taking damage.";
                        break;

                    case "Drop Beacon":
                        explanation.text = $"The Scouts(s) of the selected squadron(s) will drop a beacon, clearing away the fog of war in a small area until destroyed.";
                        break;

                    case "Match Speed":
                        explanation.text = $"{beginningActionText} all fly at the same speed: the speed of the slowest ships.";
                        break;

                    case "Attack on Sight":
                        explanation.text = $"{beginningActionText} fire upon any {side} that gets within range. This is standard behavior.";
                        break;

                    case "Cease Fire":
                        explanation.text = $"{beginningActionText} not fire on anyone under any circumstances.";
                        break;



                    case "First Seen":
                        explanation.text = $"{beginningStrategyText} {side} they see first.";
                        break;
                    case "Random":
                        explanation.text = $"The ships of the selected squadron(s) will shoot randomly at any {side} they see.";
                        if (HasSquadMaker)
                        {
                            explanation.text = $"The ships of this squadron will shoot randomly at any {side} they see.";
                        }
                        break;

                    case "Revenge":
                        explanation.text = $"{beginningStrategyText} {side} that have most recently killed our own ships.";
                        break;

                    case "Most Dangerous":
                        explanation.text = $"{beginningStrategyText} {side} that have dealt the most damage.";
                        break;

                    case "Most Health":
                        explanation.text = $"{beginningStrategyText} {side} that have the most health.";
                        break;

                    case "Least Health":
                        explanation.text = $"{beginningStrategyText} {side} that have the least health.";
                        break;

                    case "Most Powerful":
                        explanation.text = $"{beginningStrategyText} {side} that have the most fire power.";
                        break;

                    case "Least Powerful":
                        explanation.text = $"{beginningStrategyText} {side} that have the least fire power.";
                        break;

                    case "Closest":
                        explanation.text = $"{beginningStrategyText} {side} that are closest to them.";
                        break;

                    case "Furthest":
                        explanation.text = $"{beginningStrategyText} {side} that are furthest from them.";
                        break;

                    case "Most Range":
                        explanation.text = $"{beginningStrategyText} {side} that have the longest range.";
                        break;

                    case "Least Range":
                        explanation.text = $"{beginningStrategyText} {side} that have the shortest range.";
                        break;

                    case "Fastest":
                        explanation.text = $"{beginningStrategyText} fastest {side}.";
                        break;

                    case "Slowest":
                        explanation.text = $"{beginningStrategyText} slowest {side}.";
                        break;

                    case "Most Valuable":
                        explanation.text = $"{beginningStrategyText} {side} that have the most estimated strategic value.";
                        break;

                    case "Least Valuable":
                        explanation.text = $"{beginningStrategyText} {side} that have the least estimated strategic value.";
                        break;

                    case "Barges":
                    case "Carriers":
                    case "Cruisers":
                    case "Dreadnoughts":
                    case "Drones":
                    case "Factories":
                    case "Fire Ships":
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
                        explanation.text = $"{beginningStrategyText} {button}.";
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
                return Level.GetState().HasSelectedSquads;
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
                            break;

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
                    }
                }
                DeselectButton();
            }
                
        }
        public void DropBeacon()
        {
            if (HasSquad())
            {
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    foreach (Ship ship in squad.GetShips().Where((s) => s.ShipType == "Scout"))
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
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    foreach (Ship ship in squad.GetShips().Where((s) => s.ShipType == "Barge"))
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
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    squad.GetShips().Where((s) => s.ShipType == "Fire Ship").ToList().ForEach((ship) =>
                    {
                        FireShip fireShip = (FireShip)ship;
                        fireShip.Detonate();
                    });
                });
            }

        }
        public void Hold()
        {
            if (HasSquad())
            {
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
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
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
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
                Level.InputManager.SetSelectGuardTargetActive();
                HighlightSelectedButtons();
            }

        }
        public void Patrol()
        {
            if (HasSquad())
            {
                Level.InputManager.SetPatrolAreaActive();
                HighlightSelectedButtons();
            }

        }
        public void CeaseFire()
        {
            if (HasSquad())
            {
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    squad.CeaseFire = true;
                });
                HighlightSelectedButtons();
            }

        }
        public void AttackOnSight()
        {
            if (HasSquad())
            {
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    squad.CeaseFire = false;
                });
                HighlightSelectedButtons();
            }

        }
        public void MatchSpeed()
        {
            if (HasSquad())
            {
                List<Squad> squads = Level.GetState().GetSelectedSquads();
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
            if (HasSquad())
            {
                
                ShipTypes().ToList().ForEach((shipType) =>
                {
                    if (strategy.Contains(shipType) || (strategy == "Factories" && shipType == "Factory"))
                    {
                        strategy = $"Type {Utilities.ConvertShipNameToType(shipType)}";
                    }
                });

                if (HasSquadMaker)
                {
                    SavedSquad currentSquad = _squadMaker.GetCurrentSquad();
                    currentSquad.SetChanged(true);
                    currentSquad.ChosenShootingStrategy = strategy;
                }
                else if (HasLevel)
                {
                    Level.GetState().GetSelectedSquads().ForEach((squad) =>
                    {
                        //Debug.Log($"Setting the squad to shoot with {strategy}!");
                        squad.SetShootingStrategy(strategy);
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
            if (!_autoSetDropdownValue)
            {
                //Debug.Log($"Set type strategy {strategy}");
                TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
                if (strategy < dropdown.options.Count)
                {
                    string shipName = dropdown.options[strategy].text;

                    if (strategy == 0) // "-------" No option chosen
                    {
                        shipName = ConfigData.StartingSettings.DefaultShootingStrategy;
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