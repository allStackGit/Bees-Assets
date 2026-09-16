using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Owns the feedback-driven Squad Maker tutorial and repairs squad/fleet bookkeeping after
    /// deletion. The existing SquadMakerInteractionGuard remains authoritative for drag/drop.
    /// </summary>
    [DefaultExecutionOrder(1100)]
    public sealed class SquadMakerFeedbackAdjustmentGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string SavedSquadPrefix = "Saved Squad - ";
        private const string ChosenSquadPrefix = "Chosen Squad - ";
        private const float RepairInterval = 0.15f;
        private static readonly MethodInfo SetupFleetListMethod = typeof(SquadMaker).GetMethod(
            "SetupFleetList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ColorSavedSquadsMethod = typeof(SquadMaker).GetMethod(
            "ColorSavedSquadsByShipsAlive",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private SquadMaker _squadMaker;
        private float _nextRepair;
        private bool _destroyedAlertUpdated;
        private bool _plutoThreePremadesHandled;
        private bool _tutorialShown;
        private Tooltip _tutorial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SquadMakerSceneName)
            {
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                SquadMaker squadMaker = roots[i].GetComponentInChildren<SquadMaker>(true);
                if (squadMaker == null)
                {
                    continue;
                }

                SquadMakerFeedbackAdjustmentGuard guard =
                    squadMaker.GetComponent<SquadMakerFeedbackAdjustmentGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerFeedbackAdjustmentGuard>();
                }
                guard.Initialize(squadMaker);
                return;
            }
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                Initialize(GetComponent<SquadMaker>());
            }
        }

        private void Initialize(SquadMaker squadMaker)
        {
            _squadMaker = squadMaker;
            _nextRepair = 0f;
            _destroyedAlertUpdated = false;
            _plutoThreePremadesHandled = false;
            _tutorialShown = false;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null || ConfigData.CurrentShips == null ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null)
            {
                return;
            }

            UpdateDestroyedSquadAlert();
            HandlePlutoThreePremadeSquads();
            ShowSquadMakerTutorialWhenAppropriate();

            if (Time.unscaledTime < _nextRepair)
            {
                return;
            }
            _nextRepair = Time.unscaledTime + RepairInterval;

            bool repaired = RepairOrphanedSquadMembership();
            RemoveStaleSquadRows(_squadMaker.SavedSquadList, SavedSquadPrefix);
            RemoveStaleSquadRows(_squadMaker.ChosenSquadList, ChosenSquadPrefix);
            if (repaired)
            {
                RefreshFleetAndSquadPresentation();
            }
        }

        private void UpdateDestroyedSquadAlert()
        {
            if (_destroyedAlertUpdated || _squadMaker.ChoosingDeadSquadAlert == null)
            {
                return;
            }

            const string message = "All of the ships in this squad have been destroyed.";
            ConfigData.Configuration.ChoosingDeadSquadAlert = message;
            _squadMaker.ChoosingDeadSquadAlert.SetExplanation(message);
            _destroyedAlertUpdated = true;
        }

        private bool RepairOrphanedSquadMembership()
        {
            List<SavedSquad> savedSquads = ConfigData.CurrentShips.GetSavedSquads();
            HashSet<long> referencedFleetIds = new HashSet<long>();
            for (int squadIndex = 0; squadIndex < savedSquads.Count; squadIndex++)
            {
                SavedSquad squad = savedSquads[squadIndex];
                if (squad == null)
                {
                    continue;
                }

                List<SquadShip> ships = squad.GetSquadShips();
                for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                {
                    referencedFleetIds.Add(ships[shipIndex].FleetId);
                }
            }

            // The currently edited squad is intentionally not part of CurrentShips.GetSavedSquads()
            // until the user presses Save. Its fleet ships are still legitimately reserved while the
            // editor is open; treating them as orphans returns them to the fleet list and lets the same
            // FleetShip be added repeatedly, bypassing the squad-size limit visually.
            SavedSquad workingSquad = _squadMaker.GetCurrentSquad();
            if (workingSquad != null)
            {
                List<SquadShip> workingShips = workingSquad.GetSquadShips();
                for (int shipIndex = 0; shipIndex < workingShips.Count; shipIndex++)
                {
                    referencedFleetIds.Add(workingShips[shipIndex].FleetId);
                }
            }

            bool changed = false;
            List<FleetShip> fleetShips = ConfigData.CurrentShips.GetFleetShips();
            for (int i = 0; i < fleetShips.Count; i++)
            {
                FleetShip ship = fleetShips[i];
                if (ship != null && ship.DoesBelongToSavedSquad &&
                    !referencedFleetIds.Contains(ship.Id))
                {
                    // DeleteCurrentSquad removes the SavedSquad before ClearUnsavedSquad can find
                    // its original membership. Return those now-orphaned ships to the available
                    // pool first; ReplaceDeadSquadShips below consumes them for matching holes
                    // before anything left over appears in the fleet list.
                    ship.DoesBelongToSavedSquad = false;
                    changed = true;
                }
            }

            if (!changed)
            {
                return false;
            }

            ConfigData.CurrentShips.ReplaceDeadSquadShips(false);
            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();
            return true;
        }

        private void HandlePlutoThreePremadeSquads()
        {
            if (_plutoThreePremadesHandled || ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (missionId != 2)
            {
                _plutoThreePremadesHandled = true;
                return;
            }

            List<SavedSquad> candidates = ConfigData.CurrentShips
                .GetSavedSquadsBySide(ConfigData.Configuration.HumanSide)
                .Where(squad => squad != null && squad.Stats.BattlesFought == 0 &&
                                !string.IsNullOrEmpty(squad.Name) &&
                                squad.Name.StartsWith("Squad #", StringComparison.Ordinal))
                .OrderByDescending(squad => squad.Id)
                .Take(3)
                .ToList();

            if (candidates.Count != 3 ||
                candidates.Max(squad => squad.Id) - candidates.Min(squad => squad.Id) != 2 ||
                !ContainsExactComposition(candidates, ConfigData.ShipTypes.Dreadnought, 3) ||
                !ContainsExactComposition(candidates, ConfigData.ShipTypes.Frigate, 3) ||
                !ContainsExactComposition(candidates, ConfigData.ShipTypes.Scout, 1))
            {
                // Either this save predates the automatic Pluto III squads or the player has
                // already changed them. Never dissolve ambiguous user-authored squads.
                _plutoThreePremadesHandled = true;
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                SavedSquad squad = candidates[i];
                List<SquadShip> squadShips = squad.GetSquadShips();
                for (int shipIndex = 0; shipIndex < squadShips.Count; shipIndex++)
                {
                    FleetShip fleetShip = ConfigData.CurrentShips.GetFleetShip(squadShips[shipIndex].FleetId);
                    if (fleetShip != null)
                    {
                        fleetShip.DoesBelongToSavedSquad = false;
                    }
                }
                ConfigData.CurrentShips.RemoveSquad(squad);
            }

            // Consistent with normal squad deletion, released ships first repair any dead/unfilled
            // squads of matching type. Everything else remains available for the player to build
            // Pluto III squads themselves.
            ConfigData.CurrentShips.ReplaceDeadSquadShips(false);
            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();
            RemoveStaleSquadRows(_squadMaker.SavedSquadList, SavedSquadPrefix);
            RemoveStaleSquadRows(_squadMaker.ChosenSquadList, ChosenSquadPrefix);
            RefreshFleetAndSquadPresentation();
            _plutoThreePremadesHandled = true;
        }

        private static bool ContainsExactComposition(
            List<SavedSquad> squads,
            ConfigData.ShipTypes type,
            int count)
        {
            for (int i = 0; i < squads.Count; i++)
            {
                List<SquadShip> ships = squads[i].GetSquadShips();
                if (ships.Count == count && ships.All(ship => ship.ShipType == type))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RemoveStaleSquadRows(GameObject list, string prefix)
        {
            if (list == null)
            {
                return;
            }

            Transform parent = list.transform;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (child == null || !child.name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                int marker = child.name.LastIndexOf('#');
                if (marker < 0 || !int.TryParse(child.name.Substring(marker + 1), out int squadId))
                {
                    continue;
                }

                if (ConfigData.CurrentShips.GetSavedSquad(squadId) == null)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        private void RefreshFleetAndSquadPresentation()
        {
            SetupFleetListMethod?.Invoke(_squadMaker, null);
            ColorSavedSquadsMethod?.Invoke(_squadMaker, null);
        }

        private void ShowSquadMakerTutorialWhenAppropriate()
        {
            if (_tutorialShown || ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                !ConfigData.UserProgressData.ShowToolTips ||
                _squadMaker.Side != ConfigData.Configuration.HumanSide)
            {
                return;
            }

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (missionId != 1)
            {
                _tutorialShown = true;
                return;
            }

            _tutorial = CreateTutorialBox();
            if (_tutorial == null)
            {
                return;
            }

            _tutorialShown = true;
            _tutorial.Place(Vector2.zero, new Vector2(430f, 180f));
            _tutorial.ShowSequence(new List<string>
            {
                "The squad list contains the preexisting squads in your fleet. Select a squad there when you want to use or work with a squad you already have.",
                "To edit an existing squad, drag it from the squad list into the squad editor. You can then change its ships, formation, name, or color and save the changes.",
                "To make a new squad, drag available ships from the fleet list into the squad editor, arrange the squad, give it a name, and save it.",
                "You can drag and drop squads between the squad list and the squad editor instead of using the load controls.",
                "You can also drag and drop squads between the squad list and the chosen squad list. The chosen squad list contains the squads that will enter the next mission."
            }, true, () =>
            {
                if (_tutorial != null)
                {
                    Destroy(_tutorial.gameObject);
                    _tutorial = null;
                }
            });
        }

        private Tooltip CreateTutorialBox()
        {
            Canvas canvas = _squadMaker.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isRootCanvas)
            {
                Canvas[] canvases = FindObjectsOfType<Canvas>(true);
                canvas = canvases.FirstOrDefault(candidate => candidate != null && candidate.isRootCanvas);
            }
            if (canvas == null)
            {
                return null;
            }

            TMP_FontAsset font = null;
            if (_squadMaker.TooltipText != null)
            {
                TMP_Text sourceText = _squadMaker.TooltipText.GetComponent<TMP_Text>();
                if (sourceText != null)
                {
                    font = sourceText.font;
                }
            }
            if (font == null)
            {
                font = TMP_Settings.defaultFontAsset;
            }

            GameObject root = new GameObject(
                "Squad Maker Tutorial",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            root.transform.SetParent(canvas.transform, false);
            root.transform.SetAsLastSibling();
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(430f, 180f);
            root.GetComponent<Image>().color = new Color(0.10f, 0.12f, 0.14f, 0.97f);

            GameObject textObject = new GameObject(
                "Text",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            textObject.transform.SetParent(root.transform, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = 17f;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = true;
            text.raycastTarget = false;

            GameObject closeObject = new GameObject(
                "Close Button",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            closeObject.transform.SetParent(root.transform, false);
            RectTransform closeRect = closeObject.GetComponent<RectTransform>();
            closeRect.anchorMin = Vector2.one;
            closeRect.anchorMax = Vector2.one;
            closeRect.pivot = Vector2.one;
            closeRect.anchoredPosition = new Vector2(-8f, -8f);
            closeRect.sizeDelta = new Vector2(24f, 24f);
            closeObject.GetComponent<Image>().color = new Color(0.34f, 0.39f, 0.44f, 0.96f);

            GameObject closeLabel = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            closeLabel.transform.SetParent(closeObject.transform, false);
            RectTransform closeLabelRect = closeLabel.GetComponent<RectTransform>();
            closeLabelRect.anchorMin = Vector2.zero;
            closeLabelRect.anchorMax = Vector2.one;
            closeLabelRect.offsetMin = Vector2.zero;
            closeLabelRect.offsetMax = Vector2.zero;
            TextMeshProUGUI closeText = closeLabel.GetComponent<TextMeshProUGUI>();
            closeText.font = font;
            closeText.fontSize = 16f;
            closeText.text = "×";
            closeText.color = Color.white;
            closeText.alignment = TextAlignmentOptions.Center;
            closeText.raycastTarget = false;

            Tooltip tooltip = root.AddComponent<Tooltip>();
            tooltip.TooltipObject = root;
            tooltip.TooltipPosition = rootRect;
            tooltip.TooltipSize = rootRect;
            tooltip.TooltipText = text;
            tooltip.CloseButton = closeObject;
            return tooltip;
        }
    }
}
