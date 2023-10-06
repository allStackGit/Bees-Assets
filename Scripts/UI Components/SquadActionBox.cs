


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
        public GameObject MatchSpeedButton, CeaseFireButton, AttackOnSightButton, PatrolButton, GuardButton, ChaseButton, HoldButton, DetonateButton, 
            TypeSelector, ActionTitle, ActionExplanation;

        private EventSystem _eventSystem;
        private SquadMaker _squadMaker = null;
        private LevelStage Level = null;
        private string _blankShipType = "———————";
        private bool _autoSetDropdownValue = false;

        private bool HasSquadMaker => _squadMaker != null;
        private bool HasLevel => Level != null;    

        public void Setup(SquadMaker squadMaker, EventSystem eventSystem)
        {
            this._squadMaker = squadMaker;
            _eventSystem = eventSystem;
            Destroy(PatrolButton);
            Destroy(GuardButton);
            Destroy(ChaseButton);
            Destroy(HoldButton);
            Destroy(DetonateButton);
            ActualSetup();
            
        }
        public void Setup(LevelStage level, EventSystem eventSystem)
        {
            this.Level = level;
            _eventSystem = eventSystem;
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
                squadText.text = GetSquadNames();
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
            BeeShipTypes().ToList().ForEach(bee =>
            {
                //Debugger.Log("Setting drop down option");
                if (bee != "Queen")
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData($"{bee}s"));
                }
                else
                {
                    dropdown.options.Add(new TMP_Dropdown.OptionData($"{bee}"));
                }
            });
        }
        private List<string> BeeShipTypes()
        {
            if (HasSquadMaker)
            {
                return ConfigData.Configuration.VisibleBeeShipTypes.ToList();
            }
            else if (HasLevel)
            {
                return Level.GetState().GetBeeShipTypes().ToList();
            }
            return new List<string>();
        }
        private void SetDropdownValue()
        {
            
            string shootingStrategy = GetShootingStrategy();
            TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
            _autoSetDropdownValue = true;

            if (shootingStrategy.StartsWith("Type "))
            {
                string shipName = Utilities.ConvertShipTypeToPluralName(shootingStrategy);
                //Debugger.Log($"Ship name is {shipName}");
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
                    //Debugger.Log($"Shooting strategy is {shootingStrategy}");
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
            if (HasLevel)
            {
                bool hasFireShip = false;
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    if (squad.GetShips().Any((s) => s.ShipType == "Fire Ship"))
                    {
                        hasFireShip = true;
                    }
                });
                DetonateButton.SetActive(hasFireShip);

                if (IsAction("Patrol"))
                {
                    HighlightButton(PatrolButton);
                }
                else
                {
                    //Debugger.Log($"Not patrolling: {Level.GetState().GetSelectedSquads().First().GetCommandStrategy()}");
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
                if (IsAction("Chase"))
                {
                    HighlightButton(ChaseButton);
                }
                else
                {
                    ResetButton(ChaseButton);
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
            //Debugger.Log($"Squad(s) shooting strategy: {shootingStrategy}");
            ConfigData.Configuration.ShootingStrategies.Where(s => !s.StartsWith("Type ")).ToList().ForEach(s =>
            {
                //Debugger.Log($"{s} Button");
                GameObject buttonLabel = GameObject.Find($"{s} Button");
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
            //Debugger.Log($"Highlighting {buttonLabel.name}");
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
            //Debugger.Log($"Resetting {buttonLabel}");
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
                //Debugger.Log($"Setting text for {button}");
                TMP_Text title = ActionTitle.GetComponentInChildren<TMP_Text>();
                TMP_Text explanation = ActionExplanation.GetComponentInChildren<TMP_Text>();
                title.text = button;
                
                string beginningActionText = "The selected squadron(s) will";
                string beginningStrategyText = "When there are multiple Bees within range, the ships of the selected squadron(s) will prioritize shooting at the";

                if (HasSquadMaker)
                {
                    beginningActionText = "This squadron will";
                    beginningStrategyText = "When there are multiple Bees within range, the ships of this squadron will prioritize shooting at the";
                }
                switch (button)
                {
                    case "Patrol":
                        explanation.text = $"{beginningActionText} patrol around the border that you select (by selecting an area), engaging any Bees they encounter.";
                        break;

                    case "Guard":
                        explanation.text = $"{beginningActionText} guard the squadron you select (by right clicking on it) by flying nearby and following it.";
                        break;

                    case "Chase":
                        explanation.text = $"{beginningActionText} chase down and engage the first Bees they see.";
                        break;

                    case "Hold":
                        explanation.text = $"{beginningActionText} hold their position and fire upon any Bees that gets within range.";
                        break;

                    case "Detonate":
                        explanation.text = $"The Fire Ship(s) of the selected squadron(s) will detonate their nuclear cargo, severely damaging or destroying all ships around them.";
                        break;

                    case "Match Speed":
                        explanation.text = $"{beginningActionText} all fly at the same speed: the speed of the slowest ships.";
                        break;

                    case "Attack on Sight":
                        explanation.text = $"{beginningActionText} fire upon any Bees that gets within range. This is standard behavior.";
                        break;

                    case "Cease Fire":
                        explanation.text = $"{beginningActionText} not fire on anyone under any circumstances.";
                        break;



                    case "First Seen":
                        explanation.text = $"{beginningStrategyText} Bees they see first.";
                        break;
                    case "Random":
                        explanation.text = $"The ships of the selected squadron(s) will shoot randomly at any Bees they see.";
                        if (HasSquadMaker)
                        {
                            explanation.text = $"The ships of this squadron will shoot randomly at any Bees they see.";
                        }
                        break;

                    case "Revenge":
                        explanation.text = $"{beginningStrategyText} Bees that have most recently killed our own ships.";
                        break;

                    case "Most Dangerous":
                        explanation.text = $"{beginningStrategyText} Bees that have dealt the most damage.";
                        break;

                    case "Most Health":
                        explanation.text = $"{beginningStrategyText} Bees that have the most health.";
                        break;

                    case "Least Health":
                        explanation.text = $"{beginningStrategyText} Bees that have the least health.";
                        break;

                    case "Most Powerful":
                        explanation.text = $"{beginningStrategyText} Bees that have the most fire power.";
                        break;

                    case "Least Powerful":
                        explanation.text = $"{beginningStrategyText} Bees that have the least fire power.";
                        break;

                    case "Closest":
                        explanation.text = $"{beginningStrategyText} Bees that are closest to them.";
                        break;

                    case "Furthest":
                        explanation.text = $"{beginningStrategyText} Bees that are furthest from them.";
                        break;

                    case "Most Range":
                        explanation.text = $"{beginningStrategyText} Bees that have the longest range.";
                        break;

                    case "Least Range":
                        explanation.text = $"{beginningStrategyText} Bees that have the shortest range.";
                        break;

                    case "Fastest":
                        explanation.text = $"{beginningStrategyText} fastest Bees.";
                        break;

                    case "Slowest":
                        explanation.text = $"{beginningStrategyText} slowest Bees.";
                        break;

                    case "Most Valuable":
                        explanation.text = $"{beginningStrategyText} Bees that have the most estimated strategic value.";
                        break;

                    case "Least Valuable":
                        explanation.text = $"{beginningStrategyText} Bees that have the least estimated strategic value.";
                        break;

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
                List<Squad> selectedSquads = Level.GetState().GetSelectedSquads();
                if (selectedSquads.Count > 0)
                {
                    return true;
                }
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
                //Debugger.Log($"Setting the squads to {action}!");
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
                    }

                }
                else if (HasLevel)
                {
                    switch (action)
                    {
                        case "Patrol":
                            Level.InputManager.SetPatrolAreaActive();
                            break;

                        case "Guard":
                            Level.InputManager.SetSelectGuardTargetActive();
                            break;

                        case "Chase":
                            Level.GetState().GetSelectedSquads().ForEach((squad) =>
                            {
                                squad.SetChase(true);
                            });
                            break;

                        case "Hold":
                            Level.GetState().GetSelectedSquads().ForEach((squad) =>
                            {
                                squad.StopChasing();
                            });
                            break;
                        case "Detonate":
                            Level.GetState().GetSelectedSquads().ForEach((squad) =>
                            {
                                squad.GetShips().Where((s) => s.ShipType == "Fire Ship").ToList().ForEach((ship) =>
                                {
                                    FireShip fireShip = (FireShip)ship;
                                    fireShip.Detonate();
                                });
                            });
                            break;

                        case "Match Speed":
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
                            break;

                        case "Attack on Sight":
                            Level.GetState().GetSelectedSquads().ForEach((squad) =>
                            {
                                squad.CeaseFire = false;
                            });
                            break;

                        case "Cease Fire":
                            Level.GetState().GetSelectedSquads().ForEach((squad) =>
                            {
                                squad.CeaseFire = true;
                            });
                            break;
                    }
                }
                HighlightSelectedButtons();
                DeselectButton();
            }
                
        }
        public void SetShootingStrategy(string strategy)
        {
            //Debugger.Log("Set shooting strategy");
            if (HasSquad())
            {
                
                BeeShipTypes().ToList().ForEach((bee) =>
                {
                    if (strategy.Contains(bee))
                    {
                        strategy = $"Type {Utilities.ConvertShipNameToType(bee)}";
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
                        //Debugger.Log($"Setting the squad to shoot with {strategy}!");
                        squad.SetShootingStrategy(strategy);
                    });
                }
                SetDropdownValue();
                DeselectButton();
                HighlightSelectedButtons();
            }

        }
        public void SetTypeStrategy(int strategy)
        {
            if (!_autoSetDropdownValue)
            {
                //Debugger.Log($"Set type strategy {strategy}");
                TMP_Dropdown dropdown = TypeSelector.GetComponentInChildren<TMP_Dropdown>();
                string shipName = dropdown.options[strategy].text;

                if (strategy == 0) // "-------" No option chosen
                {
                    shipName = ConfigData.StartingSettings.DefaultShootingStrategy;
                }

                SetShootingStrategy(shipName);
            }
            else
            {
                _autoSetDropdownValue = false;
            }


        }
    }
}