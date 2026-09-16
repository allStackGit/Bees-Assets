using Assets.Scripts;
using Assets.Scripts.UI_Components;
using TMPro;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps the Pluto II tutorial presentation coherent without changing the underlying mission
    /// trigger progression: objective/intro callouts stay centered, the malformed squad-number
    /// highlight is repaired before rendering, and Samuel's follow-up dialogue is held until the
    /// multi-page tactical tooltip has actually been closed.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class PlutoTwoTutorialPresentationGuard : MonoBehaviour
    {
        private const string ScoutSelectionPrompt = "Select the Scout squad with the left mouse button.";
        private DialogueManager _heldDialogueManager;
        private bool _restoreDialogueBox;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Pluto II Tutorial Presentation Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<PlutoTwoTutorialPresentationGuard>();
        }

        private void LateUpdate()
        {
            if (!IsPlutoTwoCampaign())
            {
                ReleaseHeldDialogue();
                return;
            }

            Stage stage = FindObjectOfType<Stage>();
            if (stage == null || stage.Menus == null)
            {
                ReleaseHeldDialogue();
                return;
            }

            CenterMissionStatus(stage);
            CenterScoutSelectionTooltip(stage);
            RepairOverscaledTutorialHighlight(stage);
            HoldDialogueUntilTutorialEnds(stage);
        }

        private static bool IsPlutoTwoCampaign()
        {
            return ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                ConfigData.UserProgressData != null &&
                ConfigData.Configuration != null &&
                ConfigData.UserProgressData.GetCurrentLevel(
                    ConfigData.Configuration.HumanSide,
                    ConfigData.GameModes.Campaign) == 1;
        }

        private static void CenterMissionStatus(Stage stage)
        {
            if (stage.Menus.MissionStatus == null || !stage.Menus.MissionStatus.activeInHierarchy)
            {
                return;
            }

            RectTransform statusRect = stage.Menus.MissionStatus.GetComponent<RectTransform>();
            Canvas canvas = statusRect != null ? statusRect.GetComponentInParent<Canvas>() : null;
            RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            if (statusRect == null || canvasRect == null)
            {
                return;
            }

            Vector3[] statusCorners = new Vector3[4];
            Vector3[] canvasCorners = new Vector3[4];
            statusRect.GetWorldCorners(statusCorners);
            canvasRect.GetWorldCorners(canvasCorners);
            float statusCenterX = (statusCorners[0].x + statusCorners[2].x) * 0.5f;
            float canvasCenterX = (canvasCorners[0].x + canvasCorners[2].x) * 0.5f;
            Vector3 position = statusRect.position;
            position.x += canvasCenterX - statusCenterX;
            statusRect.position = position;
        }

        private static void CenterScoutSelectionTooltip(Stage stage)
        {
            if (stage.Menus.UIOverlay == null)
            {
                return;
            }

            Tooltip[] tooltips = stage.Menus.UIOverlay.GetComponentsInChildren<Tooltip>(true);
            for (int i = 0; i < tooltips.Length; i++)
            {
                Tooltip tooltip = tooltips[i];
                if (tooltip == null || tooltip.TooltipObject == null ||
                    !tooltip.TooltipObject.activeInHierarchy || tooltip.TooltipText == null ||
                    tooltip.TooltipText.text != ScoutSelectionPrompt || tooltip.TooltipPosition == null)
                {
                    continue;
                }

                tooltip.TooltipPosition.localPosition = Vector3.zero;
            }
        }

        private static void RepairOverscaledTutorialHighlight(Stage stage)
        {
            if (stage.Menus.UIOverlay == null)
            {
                return;
            }

            RectTransform[] rects = stage.Menus.UIOverlay.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null)
                {
                    continue;
                }

                Vector3 scale = rect.localScale;
                if (Mathf.Abs(scale.x - 150f) > 0.01f || Mathf.Abs(scale.y - 30f) > 0.01f)
                {
                    continue;
                }

                // Pluto II used transform scale as though it were pixel dimensions. That expands
                // the entire highlight prefab (including child rendering) by 150x30 and is the
                // source of the screen-filling repeated "S"-looking artifact.
                rect.localScale = Vector3.one;
                rect.sizeDelta = new Vector2(150f, 30f);
            }
        }

        private void HoldDialogueUntilTutorialEnds(Stage stage)
        {
            CutsceneManager cutscene = stage.CutsceneManager;
            DialogueManager dialogueManager = cutscene != null ? cutscene.DialogueManager : null;
            bool tacticalSequenceActive = IsTutorialSequenceActive(stage);

            if (tacticalSequenceActive && dialogueManager != null && dialogueManager.DialogueBox != null &&
                dialogueManager.DialogueBox.activeSelf)
            {
                if (_heldDialogueManager == null)
                {
                    _heldDialogueManager = dialogueManager;
                    _restoreDialogueBox = true;
                }

                dialogueManager.DialogueBox.SetActive(false);
                dialogueManager.enabled = false;
                return;
            }

            if (!tacticalSequenceActive)
            {
                ReleaseHeldDialogue();
            }
        }

        private static bool IsTutorialSequenceActive(Stage stage)
        {
            if (stage.Menus.UIOverlay == null)
            {
                return false;
            }

            Tooltip[] tooltips = stage.Menus.UIOverlay.GetComponentsInChildren<Tooltip>(true);
            for (int i = 0; i < tooltips.Length; i++)
            {
                Tooltip tooltip = tooltips[i];
                if (tooltip == null || tooltip.TooltipObject == null || !tooltip.TooltipObject.activeInHierarchy)
                {
                    continue;
                }

                Transform footer = tooltip.TooltipObject.transform.Find("Tutorial Sequence Footer");
                if (footer == null && tooltip.TooltipSize != null)
                {
                    footer = tooltip.TooltipSize.Find("Tutorial Sequence Footer");
                }
                if (footer != null && footer.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            return false;
        }

        private void ReleaseHeldDialogue()
        {
            if (_heldDialogueManager == null)
            {
                _restoreDialogueBox = false;
                return;
            }

            _heldDialogueManager.enabled = true;
            if (_restoreDialogueBox && _heldDialogueManager.DialogueBox != null)
            {
                _heldDialogueManager.DialogueBox.SetActive(true);
            }

            _heldDialogueManager = null;
            _restoreDialogueBox = false;
        }
    }
}
