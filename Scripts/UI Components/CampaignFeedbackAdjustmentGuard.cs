using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Applies campaign-specific tutorial feedback without duplicating the authored mission flow.
    /// The guard only owns presentation/gating rules that need to span the existing trigger steps.
    /// </summary>
    [DefaultExecutionOrder(1200)]
    public sealed class CampaignFeedbackAdjustmentGuard : MonoBehaviour
    {
        private const int PlutoTwoMissionId = 1;
        private const int PlutoThreeMissionId = 2;
        private const int PlutoFourMissionId = 3;
        private const string FlightColorHex = "#5CC8FF";
        private const string ShootingColorHex = "#FFB347";
        private static readonly Color FlightColor = new Color(0.36f, 0.78f, 1f, 1f);
        private static readonly Color ShootingColor = new Color(1f, 0.70f, 0.28f, 1f);
        private static readonly FieldInfo DialogueTimerField = typeof(Level).GetField(
            "_dialogueTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private Stage _stage;
        private GameObject _squadNumberArrow;
        private DialogueManager _gatedDialogueManager;
        private bool _plutoTwoTutorialSeen;
        private bool _plutoTwoTutorialComplete;
        private bool _plutoTwoDialogueGated;
        private bool _dialogueManagerEnabledBeforeGate;
        private bool _dialogueBoxActiveBeforeGate;
        private bool _plutoThreeTimerAdjusted;
        private Level _trackedLevel;
        private int _trackedMissionId = -1;
        private float _trackedLevelStartTime = float.NaN;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (GameObject.Find("Campaign Feedback Adjustment Guard") != null)
            {
                return;
            }

            GameObject host = new GameObject("Campaign Feedback Adjustment Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignFeedbackAdjustmentGuard>();
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null)
            {
                ResetStageState();
                return;
            }

            if (_stage == null || _stage.PrimaryLevel == null || _stage.Menus == null)
            {
                Stage resolved = FindObjectOfType<Stage>();
                if (resolved != _stage)
                {
                    ResetStageState();
                    _stage = resolved;
                }
            }

            if (_stage == null || _stage.PrimaryLevel == null || _stage.Menus == null)
            {
                return;
            }

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            TrackMissionInstance(missionId);

            switch (missionId)
            {
                case PlutoTwoMissionId:
                    UpdatePlutoTwo();
                    break;
                case PlutoThreeMissionId:
                    UpdatePlutoThree();
                    break;
                case PlutoFourMissionId:
                    UpdatePlutoFour();
                    break;
                default:
                    RestorePlutoTwoDialogue();
                    DestroySquadNumberArrow();
                    break;
            }
        }

        private void UpdatePlutoTwo()
        {
            Tooltip activeTooltip = FindActiveTutorialTooltip();
            string text = activeTooltip != null && activeTooltip.TooltipText != null
                ? activeTooltip.TooltipText.text ?? string.Empty
                : string.Empty;

            bool selectingPage = Contains(text, "select an entire squad");
            bool settingsPage = Contains(text, "flight pattern") && Contains(text, "shooting strateg");
            bool squadNumberPage = Contains(text, "number hotkeys");
            bool tacticalSequencePage = squadNumberPage ||
                                        Contains(text, "selected ships’ range") ||
                                        Contains(text, "selected ships' range") ||
                                        Contains(text, "quickly find a selected squad") ||
                                        Contains(text, "shooting strategy and flight pattern");

            if (selectingPage || settingsPage || tacticalSequencePage)
            {
                _plutoTwoTutorialSeen = true;
            }

            if (activeTooltip != null && activeTooltip.TooltipText != null)
            {
                string correctedText = activeTooltip.TooltipText.text;
                correctedText = correctedText.Replace("ships’ range", "ships’ ranges");
                correctedText = correctedText.Replace("ships' range", "ships' ranges");

                if (settingsPage)
                {
                    correctedText = ColorizePlutoTwoSettings(correctedText);
                    KeepFirstPlayerSquadSelected();
                    MatchAndColorSettingsArrows();
                }

                if (activeTooltip.TooltipText.text != correctedText)
                {
                    activeTooltip.TooltipText.text = correctedText;
                }
            }

            if (squadNumberPage)
            {
                RemoveSquadNumberHighlight();
                EnsureSquadNumberArrow();
            }
            else
            {
                DestroySquadNumberArrow();
            }

            if (_plutoTwoTutorialSeen && !_plutoTwoTutorialComplete)
            {
                if (activeTooltip != null)
                {
                    GatePlutoTwoDialogue();
                }
                else
                {
                    _plutoTwoTutorialComplete = true;
                    RestorePlutoTwoDialogue();
                }
            }
        }

        private void UpdatePlutoThree()
        {
            RestorePlutoTwoDialogue();
            DestroySquadNumberArrow();

            if (_plutoThreeTimerAdjusted || DialogueTimerField == null)
            {
                return;
            }

            ScaledTimer dialogueTimer = DialogueTimerField.GetValue(_stage.PrimaryLevel) as ScaledTimer;
            if (dialogueTimer == null || dialogueTimer.Action == null)
            {
                return;
            }

            // Pluto III previously waited 1.5 seconds before Samuel's opening message.
            // Keep the authored dialogue itself unchanged and only remove the presentation delay.
            dialogueTimer.Length = 0f;
            dialogueTimer.Elapsed = Mathf.Max(dialogueTimer.Elapsed, 0.01f);
            _plutoThreeTimerAdjusted = true;
        }

        private void UpdatePlutoFour()
        {
            RestorePlutoTwoDialogue();
            DestroySquadNumberArrow();

            if (_stage.Menus.MissionStatusText != null &&
                _stage.Menus.MissionStatusText.text != "Defend Pluto and Survive!")
            {
                _stage.Menus.SetMissionStatus("Defend Pluto and Survive!");
            }
        }

        private Tooltip FindActiveTutorialTooltip()
        {
            if (_stage.Menus.UIOverlay == null)
            {
                return null;
            }

            Tooltip[] tooltips = _stage.Menus.UIOverlay.GetComponentsInChildren<Tooltip>(true);
            for (int i = tooltips.Length - 1; i >= 0; i--)
            {
                Tooltip tooltip = tooltips[i];
                if (tooltip != null && tooltip.TooltipObject != null &&
                    tooltip.TooltipObject.activeInHierarchy)
                {
                    return tooltip;
                }
            }
            return null;
        }

        private void KeepFirstPlayerSquadSelected()
        {
            Level level = _stage.PrimaryLevel;
            if (level.State == null)
            {
                return;
            }

            List<Squad> squads = level.State.GetSquadsBySide(ConfigData.Configuration.UserSide);
            if (squads == null || squads.Count == 0 || squads[0] == null || squads[0].IsSelected)
            {
                return;
            }

            level.State.SelectSquad(squads[0]);
        }

        private void MatchAndColorSettingsArrows()
        {
            if (_stage.Menus.UIOverlay == null || _stage.Menus.PointerArrow == null)
            {
                return;
            }

            Vector2 flightPosition = new Vector2(-447f, -238f);
            Vector2 shootingPosition = new Vector2(-330f, -340f);
            Transform flightArrow = null;
            Transform shootingArrow = null;
            float flightDistance = float.MaxValue;
            float shootingDistance = float.MaxValue;
            Transform overlay = _stage.Menus.UIOverlay.transform;

            for (int i = 0; i < overlay.childCount; i++)
            {
                Transform child = overlay.GetChild(i);
                if (child == null || child.gameObject == _squadNumberArrow ||
                    !child.name.StartsWith(_stage.Menus.PointerArrow.name, StringComparison.Ordinal))
                {
                    continue;
                }

                float toFlight = Vector2.Distance(child.localPosition, flightPosition);
                if (toFlight < flightDistance)
                {
                    flightDistance = toFlight;
                    flightArrow = child;
                }

                float toShooting = Vector2.Distance(child.localPosition, shootingPosition);
                if (toShooting < shootingDistance)
                {
                    shootingDistance = toShooting;
                    shootingArrow = child;
                }
            }

            if (flightArrow == null || shootingArrow == null || flightArrow == shootingArrow)
            {
                return;
            }

            // The second authored arrow was scaled to a different size. Preserve the first arrow's
            // intended scale and make the corresponding shooting-strategy arrow match it exactly.
            shootingArrow.localScale = flightArrow.localScale;
            SetArrowColor(flightArrow, FlightColor);
            SetArrowColor(shootingArrow, ShootingColor);
        }

        private static void SetArrowColor(Transform arrow, Color color)
        {
            Graphic[] graphics = arrow.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                graphics[i].color = color;
            }
        }

        private static string ColorizePlutoTwoSettings(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (!text.Contains("<color=" + FlightColorHex + ">"))
            {
                text = text.Replace(
                    "flight pattern",
                    "<color=" + FlightColorHex + ">flight pattern</color>");
            }
            if (!text.Contains("<color=" + ShootingColorHex + ">"))
            {
                text = text.Replace(
                    "shooting strategies",
                    "<color=" + ShootingColorHex + ">shooting strategies</color>");
            }
            return text;
        }

        private void GatePlutoTwoDialogue()
        {
            DialogueManager manager = _stage.CutsceneManager != null
                ? _stage.CutsceneManager.DialogueManager
                : null;
            if (manager == null)
            {
                return;
            }
            if (_plutoTwoDialogueGated && _gatedDialogueManager != manager)
            {
                RestorePlutoTwoDialogue();
            }

            if (!_plutoTwoDialogueGated)
            {
                _dialogueManagerEnabledBeforeGate = manager.enabled;
                _dialogueBoxActiveBeforeGate =
                    manager.DialogueBox != null && manager.DialogueBox.activeSelf;
            }
            _gatedDialogueManager = manager;
            _plutoTwoDialogueGated = true;
            manager.enabled = false;
            if (manager.DialogueBox != null && manager.DialogueBox.activeSelf)
            {
                manager.DialogueBox.SetActive(false);
            }
        }

        private void RestorePlutoTwoDialogue()
        {
            if (!_plutoTwoDialogueGated || _gatedDialogueManager == null)
            {
                _plutoTwoDialogueGated = false;
                _gatedDialogueManager = null;
                _dialogueManagerEnabledBeforeGate = false;
                _dialogueBoxActiveBeforeGate = false;
                return;
            }

            DialogueManager manager = _gatedDialogueManager;
            manager.enabled = _dialogueManagerEnabledBeforeGate;
            bool dialogueSectionActive = manager.CutsceneManager != null
                ? manager.CutsceneManager.HasActiveDialogueSection
                : _dialogueBoxActiveBeforeGate;
            if (manager.DialogueBox != null)
            {
                manager.DialogueBox.SetActive(dialogueSectionActive);
            }
            _plutoTwoDialogueGated = false;
            _gatedDialogueManager = null;
            _dialogueManagerEnabledBeforeGate = false;
            _dialogueBoxActiveBeforeGate = false;
        }

        private void RemoveSquadNumberHighlight()
        {
            if (_stage.Menus.UIOverlay == null || _stage.Menus.UIHighlightTooltipPrefab == null)
            {
                return;
            }

            string prefix = _stage.Menus.UIHighlightTooltipPrefab.name;
            Transform overlay = _stage.Menus.UIOverlay.transform;
            for (int i = overlay.childCount - 1; i >= 0; i--)
            {
                Transform child = overlay.GetChild(i);
                if (child != null && child.name.StartsWith(prefix, StringComparison.Ordinal) &&
                    Vector2.Distance(child.localPosition, new Vector2(-610f, 370f)) < 100f)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        private void EnsureSquadNumberArrow()
        {
            if (_squadNumberArrow != null || _stage.Menus.PointerArrow == null ||
                _stage.Menus.UIOverlay == null)
            {
                return;
            }

            _squadNumberArrow = Instantiate(
                _stage.Menus.PointerArrow,
                _stage.Menus.UIOverlay.transform);
            _squadNumberArrow.name = "Squad Number Tutorial Arrow";
            _squadNumberArrow.transform.localPosition = new Vector2(-455f, 273f);
            _squadNumberArrow.transform.localScale = Vector3.one;
            _squadNumberArrow.transform.localEulerAngles = new Vector3(0f, 0f, 150f);
        }

        private void DestroySquadNumberArrow()
        {
            if (_squadNumberArrow != null)
            {
                Destroy(_squadNumberArrow);
                _squadNumberArrow = null;
            }
        }

        private static bool Contains(string source, string value)
        {
            return !string.IsNullOrEmpty(source) &&
                source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TrackMissionInstance(int missionId)
        {
            Level level = _stage.PrimaryLevel;
            float levelStartTime = level.StartTime;
            if (_trackedLevel == level &&
                _trackedMissionId == missionId &&
                Mathf.Approximately(_trackedLevelStartTime, levelStartTime))
            {
                return;
            }

            ResetMissionPresentationState();
            _trackedLevel = level;
            _trackedMissionId = missionId;
            _trackedLevelStartTime = levelStartTime;
        }

        private void ResetMissionPresentationState()
        {
            RestorePlutoTwoDialogue();
            DestroySquadNumberArrow();
            _plutoTwoTutorialSeen = false;
            _plutoTwoTutorialComplete = false;
            _plutoThreeTimerAdjusted = false;
        }

        private void ResetStageState()
        {
            ResetMissionPresentationState();
            _stage = null;
            _trackedLevel = null;
            _trackedMissionId = -1;
            _trackedLevelStartTime = float.NaN;
        }
    }
}
