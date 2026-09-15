using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Stabilizes the focused regressions found while play-testing the rl/initial-design UI pass.
    /// It deliberately runs after the presentation/layout guards that own the affected UI, while
    /// remaining just ahead of PlutoIntroFeedbackGuard so the Honeybee can be frozen before that
    /// guard consumes the first-contact trigger.
    /// </summary>
    [DefaultExecutionOrder(1250)]
    public sealed class InitialDesignRegressionGuard : MonoBehaviour
    {
        private const int PlutoOneMissionId = 0;
        private const int PlutoFourMissionId = 3;
        private const float HoneybeeFreezeDuration = 4f;
        private const float TutorialHorizontalPadding = 22f;
        private const float TutorialVerticalPadding = 18f;
        private const float TutorialSequenceFooterHeight = 34f;
        private const float MinimumTutorialHeight = 90f;
        private const float PlutoHudGap = 10f;
        private const string TutorialCloseHitAreaName = "Tutorial Close Hit Area";
        private const string SummaryContinueButtonName = "Continue Button";
        private const string SavedSquadsColumnName = "Saved Squads Column";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";

        private static readonly FieldInfo SequencePagesField = typeof(Tooltip).GetField(
            "_sequencePages",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<Tooltip, Vector2> _stableSequenceSizes =
            new Dictionary<Tooltip, Vector2>();
        private readonly Dictionary<RectTransform, Vector3> _freePlayOptionPositions =
            new Dictionary<RectTransform, Vector3>();

        private SquadMaker _squadMaker;
        private Stage _stage;
        private Honeybee _frozenHoneybee;
        private Rigidbody2D _frozenHoneybeeBody;
        private bool _frozenHoneybeeWasEnabled;
        private float _honeybeeFreezeStartedAt;
        private int _capturedScreenWidth = -1;
        private int _capturedScreenHeight = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (GameObject.Find("Initial Design Regression Guard") != null)
            {
                return;
            }

            GameObject host = new GameObject("Initial Design Regression Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<InitialDesignRegressionGuard>();
        }

        private void LateUpdate()
        {
            StabilizeTutorialInfoBoxes();
            StabilizeMissionSummary();
            StabilizePlutoFourHud();
            StabilizeSquadMaker();
            UpdatePlutoOneHoneybeeFreeze();
        }

        private void StabilizeTutorialInfoBoxes()
        {
            Tooltip[] tooltips = FindObjectsOfType<Tooltip>(true);
            HashSet<Tooltip> liveSequences = new HashSet<Tooltip>();

            for (int i = 0; i < tooltips.Length; i++)
            {
                Tooltip tooltip = tooltips[i];
                if (tooltip == null || tooltip.TooltipObject == null ||
                    tooltip.TooltipText == null || tooltip.TooltipSize == null ||
                    !tooltip.TooltipObject.activeInHierarchy)
                {
                    continue;
                }

                // Main tooltip text and all generated controls should use the light-on-dark treatment.
                TMP_Text[] texts = tooltip.TooltipObject.GetComponentsInChildren<TMP_Text>(true);
                for (int textIndex = 0; textIndex < texts.Length; textIndex++)
                {
                    if (texts[textIndex] != null)
                    {
                        texts[textIndex].color = Color.white;
                    }
                }

                ConfigureExactCloseButton(tooltip.CloseButton, tooltip.Hide);

                Transform footer = tooltip.TooltipSize.Find("Tutorial Sequence Footer");
                bool sequenceActive = footer != null && footer.gameObject.activeSelf;
                if (!sequenceActive)
                {
                    _stableSequenceSizes.Remove(tooltip);
                    continue;
                }

                liveSequences.Add(tooltip);
                if (!_stableSequenceSizes.TryGetValue(tooltip, out Vector2 stableSize))
                {
                    stableSize = CalculateStableSequenceSize(tooltip);
                    _stableSequenceSizes[tooltip] = stableSize;
                }

                if (tooltip.TooltipSize.sizeDelta != stableSize)
                {
                    tooltip.TooltipSize.sizeDelta = stableSize;
                }
            }

            if (_stableSequenceSizes.Count == liveSequences.Count)
            {
                return;
            }

            List<Tooltip> stale = null;
            foreach (KeyValuePair<Tooltip, Vector2> pair in _stableSequenceSizes)
            {
                if (pair.Key == null || !liveSequences.Contains(pair.Key))
                {
                    stale ??= new List<Tooltip>();
                    stale.Add(pair.Key);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    _stableSequenceSizes.Remove(stale[i]);
                }
            }
        }

        private static Vector2 CalculateStableSequenceSize(Tooltip tooltip)
        {
            float width = Mathf.Max(1f, tooltip.TooltipSize.sizeDelta.x);
            if (width <= 1f)
            {
                width = Mathf.Max(1f, tooltip.TooltipSize.rect.width);
            }

            float contentWidth = Mathf.Max(1f, width - TutorialHorizontalPadding * 2f);
            float maxHeight = Mathf.Max(MinimumTutorialHeight, tooltip.TooltipSize.sizeDelta.y);
            List<string> pages = SequencePagesField?.GetValue(tooltip) as List<string>;
            if (pages == null || pages.Count == 0)
            {
                return new Vector2(width, maxHeight);
            }

            for (int i = 0; i < pages.Count; i++)
            {
                string page = TutorialFeedbackPolishGuard.PutSentencesOnSeparateLines(pages[i] ?? string.Empty);
                float preferredHeight = tooltip.TooltipText.GetPreferredValues(page, contentWidth, 0f).y;
                maxHeight = Mathf.Max(
                    maxHeight,
                    preferredHeight + TutorialVerticalPadding * 2f + TutorialSequenceFooterHeight);
            }

            return new Vector2(width, maxHeight);
        }

        private void StabilizeMissionSummary()
        {
            GameMenus menus = FindObjectOfType<GameMenus>();
            if (menus == null || menus.SummaryPanel == null)
            {
                return;
            }

            Transform closeTransform = FindCloseTransform(menus.SummaryPanel.transform);
            if (closeTransform != null)
            {
                ConfigureExactCloseButton(closeTransform.gameObject, menus.HideMissionSummary);
            }

            EnsureSummaryNextButton(menus);
        }

        private static Transform FindCloseTransform(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            Transform fallback = null;
            Transform[] descendants = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < descendants.Length; i++)
            {
                Transform candidate = descendants[i];
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.name == "Close Button" || candidate.name == "Close Button Stable")
                {
                    return candidate;
                }

                if (fallback == null &&
                    candidate.name.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fallback = candidate;
                }
            }
            return fallback;
        }

        private static void ConfigureExactCloseButton(GameObject closeObject, UnityEngine.Events.UnityAction action)
        {
            if (closeObject == null)
            {
                return;
            }

            Transform hitArea = closeObject.transform.Find(TutorialCloseHitAreaName);
            if (hitArea != null)
            {
                Graphic hitGraphic = hitArea.GetComponent<Graphic>();
                if (hitGraphic != null)
                {
                    hitGraphic.raycastTarget = false;
                }
                hitArea.gameObject.SetActive(false);
            }

            Button button = closeObject.GetComponent<Button>();
            if (button == null)
            {
                button = closeObject.AddComponent<Button>();
            }

            EventTrigger trigger = closeObject.GetComponent<EventTrigger>();
            if (trigger != null)
            {
                trigger.enabled = false;
            }

            Graphic target = ResolveVisibleCloseGraphic(closeObject);
            if (target == null)
            {
                return;
            }

            Graphic[] graphics = closeObject.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] != null)
                {
                    graphics[i].raycastTarget = graphics[i] == target;
                }
            }

            target.raycastTarget = true;
            button.targetGraphic = target;
            button.transition = Selectable.Transition.ColorTint;
            Color normal = target.color;
            Color hover = GetHoverColor(normal);
            Color pressed = Color.Lerp(hover, normal, 0.35f);
            pressed.a = normal.a;
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = hover;
            colors.selectedColor = hover;
            colors.pressedColor = pressed;
            colors.disabledColor = new Color(normal.r, normal.g, normal.b, normal.a * 0.45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            if (action != null)
            {
                button.onClick.RemoveListener(action);
                button.onClick.AddListener(action);
            }
        }

        private static Graphic ResolveVisibleCloseGraphic(GameObject closeObject)
        {
            Graphic rootGraphic = closeObject.GetComponent<Graphic>();
            if (IsVisibleGraphic(rootGraphic))
            {
                return rootGraphic;
            }

            TMP_Text[] labels = closeObject.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                TMP_Text label = labels[i];
                if (label != null && !string.IsNullOrWhiteSpace(label.text) &&
                    (label.text.Contains("×") || label.text.Trim().Equals("x", StringComparison.OrdinalIgnoreCase)))
                {
                    return label;
                }
            }

            Graphic[] graphics = closeObject.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] != null && graphics[i].gameObject.name != TutorialCloseHitAreaName &&
                    IsVisibleGraphic(graphics[i]))
                {
                    return graphics[i];
                }
            }
            return null;
        }

        private static bool IsVisibleGraphic(Graphic graphic)
        {
            return graphic != null && graphic.color.a > 0.05f && graphic.gameObject.activeInHierarchy;
        }

        private static Color GetHoverColor(Color normal)
        {
            float luminance = (normal.r + normal.g + normal.b) / 3f;
            Color hover = luminance > 0.72f
                ? Color.Lerp(normal, Color.black, 0.25f)
                : Color.Lerp(normal, Color.white, 0.30f);
            hover.a = normal.a;
            return hover;
        }

        private static void EnsureSummaryNextButton(GameMenus menus)
        {
            Transform existing = menus.SummaryPanel.transform.Find(SummaryContinueButtonName);
            GameObject buttonObject = existing != null ? existing.gameObject : null;
            if (buttonObject == null)
            {
                buttonObject = new GameObject(
                    SummaryContinueButtonName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(Button));
                buttonObject.transform.SetParent(menus.SummaryPanel.transform, false);

                RectTransform rect = buttonObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 18f);
                rect.sizeDelta = new Vector2(160f, 38f);

                Image background = buttonObject.GetComponent<Image>();
                background.color = new Color(0.20f, 0.24f, 0.28f, 0.96f);

                GameObject labelObject = new GameObject(
                    "Label",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));
                labelObject.transform.SetParent(buttonObject.transform, false);
                RectTransform labelRect = labelObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }

            Button button = buttonObject.GetComponent<Button>();
            if (button == null)
            {
                button = buttonObject.AddComponent<Button>();
            }
            Image image = buttonObject.GetComponent<Image>();
            if (image == null)
            {
                image = buttonObject.AddComponent<Image>();
                image.color = new Color(0.20f, 0.24f, 0.28f, 0.96f);
            }
            button.targetGraphic = image;
            button.onClick.RemoveListener(menus.HideMissionSummary);
            button.onClick.AddListener(menus.HideMissionSummary);

            TMP_Text label = buttonObject.GetComponentInChildren<TMP_Text>(true);
            if (label == null)
            {
                GameObject labelObject = new GameObject(
                    "Label",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));
                labelObject.transform.SetParent(buttonObject.transform, false);
                RectTransform labelRect = labelObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                label = labelObject.GetComponent<TextMeshProUGUI>();
            }

            TMP_Text summaryText = menus.SummaryPanel.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(text => text != null && text != label);
            if (summaryText != null)
            {
                label.font = summaryText.font;
            }
            label.text = "NEXT";
            label.fontSize = 18f;
            label.fontStyle = FontStyles.Bold;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            buttonObject.transform.SetAsLastSibling();
        }

        private void StabilizePlutoFourHud()
        {
            if (!IsCampaignMission(PlutoFourMissionId))
            {
                return;
            }

            GameMenus menus = FindObjectOfType<GameMenus>();
            if (menus == null || menus.GameSpeedButton == null || menus.PlutoShield == null ||
                !menus.GameSpeedButton.activeInHierarchy || !menus.PlutoShield.activeInHierarchy)
            {
                return;
            }

            RectTransform speed = menus.GameSpeedButton.GetComponent<RectTransform>();
            RectTransform shield = menus.PlutoShield.GetComponent<RectTransform>();
            if (speed == null || shield == null)
            {
                return;
            }

            float x = shield.anchoredPosition.x + ((shield.rect.width - speed.rect.width) * 0.5f);
            float y;
            RectTransform counter = menus.Counter != null && menus.Counter.activeInHierarchy
                ? menus.Counter.GetComponent<RectTransform>()
                : null;
            if (counter != null)
            {
                y = counter.anchoredPosition.y + ((counter.rect.height - speed.rect.height) * 0.5f);
            }
            else
            {
                y = shield.anchoredPosition.y - ((shield.rect.height + speed.rect.height) * 0.5f) - PlutoHudGap;
            }

            speed.anchoredPosition = new Vector2(x, y);
        }

        private void StabilizeSquadMaker()
        {
            if (_squadMaker == null)
            {
                _squadMaker = FindObjectOfType<SquadMaker>();
                _freePlayOptionPositions.Clear();
                _capturedScreenWidth = -1;
                _capturedScreenHeight = -1;
            }
            if (_squadMaker == null)
            {
                return;
            }

            EqualizeSquadListColumns();
            NormalizeSquadRowLabels();

            if (ConfigData.CurrentGameMode != ConfigData.GameModes.FreePlay)
            {
                _freePlayOptionPositions.Clear();
                return;
            }

            Canvas.ForceUpdateCanvases();
            bool resolutionChanged = Screen.width != _capturedScreenWidth || Screen.height != _capturedScreenHeight;
            if (_freePlayOptionPositions.Count == 0 || resolutionChanged)
            {
                CaptureFreePlayOptionPositions();
                _capturedScreenWidth = Screen.width;
                _capturedScreenHeight = Screen.height;
                return;
            }

            foreach (KeyValuePair<RectTransform, Vector3> pair in _freePlayOptionPositions)
            {
                if (pair.Key != null && pair.Key.gameObject.activeInHierarchy)
                {
                    pair.Key.position = pair.Value;
                }
            }
        }

        private void EqualizeSquadListColumns()
        {
            RectTransform savedColumn = FindAncestorByName(
                _squadMaker.SavedSquadList != null ? _squadMaker.SavedSquadList.transform : null,
                SavedSquadsColumnName);
            RectTransform chosenColumn = FindAncestorByName(
                _squadMaker.ChosenSquadList != null ? _squadMaker.ChosenSquadList.transform : null,
                ChosenSquadsColumnName);
            if (savedColumn == null || chosenColumn == null || savedColumn.parent != chosenColumn.parent)
            {
                return;
            }

            ConfigureEqualWidth(savedColumn);
            ConfigureEqualWidth(chosenColumn);
            RectTransform owner = savedColumn.parent as RectTransform;
            if (owner != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(owner);
            }
        }

        private static void ConfigureEqualWidth(RectTransform column)
        {
            LayoutElement element = column.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = column.gameObject.AddComponent<LayoutElement>();
            }
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = 1f;
        }

        private void NormalizeSquadRowLabels()
        {
            RectTransform savedTemplate = _squadMaker.SavedSquadPrefab != null
                ? _squadMaker.SavedSquadPrefab.transform as RectTransform
                : null;
            RectTransform chosenTemplate = _squadMaker.ChosenSquadPrefab != null
                ? _squadMaker.ChosenSquadPrefab.transform as RectTransform
                : null;
            if (savedTemplate == null && chosenTemplate == null)
            {
                return;
            }

            RectTransform styleSource = savedTemplate != null ? savedTemplate : chosenTemplate;
            float savedHeight = savedTemplate != null ? Mathf.Abs(savedTemplate.rect.height) : 0f;
            float chosenHeight = chosenTemplate != null ? Mathf.Abs(chosenTemplate.rect.height) : 0f;
            float sharedHeight = Mathf.Max(savedHeight, chosenHeight);
            if (sharedHeight <= 0.01f)
            {
                sharedHeight = Mathf.Max(
                    savedTemplate != null ? Mathf.Abs(savedTemplate.sizeDelta.y) : 0f,
                    chosenTemplate != null ? Mathf.Abs(chosenTemplate.sizeDelta.y) : 0f);
            }

            NormalizeSquadRow(styleSource, savedTemplate, sharedHeight);
            NormalizeSquadRow(styleSource, chosenTemplate, sharedHeight);
            NormalizeSquadListChildren(styleSource, _squadMaker.SavedSquadList, sharedHeight);
            NormalizeSquadListChildren(styleSource, _squadMaker.ChosenSquadList, sharedHeight);
        }

        private static void NormalizeSquadListChildren(RectTransform styleSource, GameObject list, float height)
        {
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.transform.childCount; i++)
            {
                NormalizeSquadRow(styleSource, list.transform.GetChild(i) as RectTransform, height);
            }
        }

        private static void NormalizeSquadRow(RectTransform styleSource, RectTransform row, float height)
        {
            if (row == null)
            {
                return;
            }

            if (height > 0.01f)
            {
                LayoutElement element = row.GetComponent<LayoutElement>();
                if (element == null)
                {
                    element = row.gameObject.AddComponent<LayoutElement>();
                }
                element.minHeight = height;
                element.preferredHeight = height;
                element.flexibleHeight = 0f;
            }

            row.localScale = Vector3.one;
            if (styleSource == null || styleSource == row)
            {
                return;
            }

            TMP_Text[] sourceTexts = styleSource.GetComponentsInChildren<TMP_Text>(true);
            TMP_Text[] targetTexts = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < targetTexts.Length; i++)
            {
                TMP_Text target = targetTexts[i];
                TMP_Text source = sourceTexts.FirstOrDefault(candidate =>
                    candidate != null && target != null && candidate.gameObject.name == target.gameObject.name);
                if (source == null || target == null)
                {
                    continue;
                }

                target.font = source.font;
                target.fontSize = source.fontSize;
                target.fontStyle = source.fontStyle;
                target.alignment = source.alignment;
                target.margin = source.margin;
                target.enableWordWrapping = source.enableWordWrapping;
            }
        }

        private void CaptureFreePlayOptionPositions()
        {
            _freePlayOptionPositions.Clear();
            AddOptionPosition(_squadMaker.FogOfWarLabel);
            AddOptionPosition(_squadMaker.FogOfWarDropdown);
            AddOptionPosition(_squadMaker.MiningLabel);
            AddOptionPosition(_squadMaker.MiningDropdown);
            AddOptionPosition(_squadMaker.EnemyReinforcementsLabel);
            AddOptionPosition(_squadMaker.EnemyReinforcementsDropdown);
            AddOptionPosition(_squadMaker.MapLabel);
            AddOptionPosition(_squadMaker.MapDropdown);
            AddOptionPosition(_squadMaker.AsteroidsLabel);
            AddOptionPosition(_squadMaker.AsteroidsDropdown);
            AddOptionPosition(_squadMaker.ObstaclesLabel);
            AddOptionPosition(_squadMaker.ObstaclesDropdown);
            AddOptionPosition(_squadMaker.OpposingForceLabel);
            AddOptionPosition(_squadMaker.OpposingForcePresetDropdown);
            AddOptionPosition(_squadMaker.ChosenEnemyShipTypeLabel);
            AddOptionPosition(_squadMaker.ChosenEnemyShipTypesDropdown);
            AddOptionPosition(_squadMaker.ChooseLevelLabel);
            AddOptionPosition(_squadMaker.LevelDropdown != null ? _squadMaker.LevelDropdown.gameObject : null);
            AddOptionPosition(_squadMaker.LevelTitleContainer);
            AddOptionPosition(_squadMaker.LevelDetailsContainer);
        }

        private void AddOptionPosition(GameObject option)
        {
            RectTransform rect = option != null ? option.transform as RectTransform : null;
            if (rect != null && !_freePlayOptionPositions.ContainsKey(rect))
            {
                _freePlayOptionPositions.Add(rect, rect.position);
            }
        }

        private void UpdatePlutoOneHoneybeeFreeze()
        {
            if (_frozenHoneybee != null)
            {
                if (_frozenHoneybeeBody != null)
                {
                    _frozenHoneybeeBody.linearVelocity = Vector2.zero;
                }

                if (!IsCampaignMission(PlutoOneMissionId) ||
                    _frozenHoneybee.IsDead ||
                    Time.unscaledTime - _honeybeeFreezeStartedAt >= HoneybeeFreezeDuration)
                {
                    ReleaseHoneybee();
                }
                return;
            }

            if (!IsCampaignMission(PlutoOneMissionId))
            {
                _stage = null;
                return;
            }

            if (_stage == null || _stage.PrimaryLevel == null || _stage.Camera == null)
            {
                _stage = FindObjectOfType<Stage>();
            }
            if (_stage == null || _stage.PrimaryLevel == null || _stage.PrimaryLevel.State == null)
            {
                return;
            }

            Scout scout = _stage.CameraShip as Scout;
            if (scout == null || scout.Squad == null || scout.Squad.CanAcceptUserInput ||
                !_stage.IsFollowingShip)
            {
                return;
            }

            Honeybee honeybee = _stage.PrimaryLevel.State.GetBeeShips()
                .OfType<Honeybee>()
                .FirstOrDefault(bee => bee != null && !bee.IsDead);
            if (honeybee == null)
            {
                return;
            }

            _frozenHoneybee = honeybee;
            _frozenHoneybeeBody = honeybee.GetComponent<Rigidbody2D>();
            _frozenHoneybeeWasEnabled = honeybee.enabled;
            _honeybeeFreezeStartedAt = Time.unscaledTime;
            if (_frozenHoneybeeBody != null)
            {
                _frozenHoneybeeBody.linearVelocity = Vector2.zero;
            }
            honeybee.enabled = false;
        }

        private void ReleaseHoneybee()
        {
            if (_frozenHoneybee != null)
            {
                if (_frozenHoneybeeBody != null)
                {
                    _frozenHoneybeeBody.linearVelocity = Vector2.zero;
                }
                _frozenHoneybee.enabled = _frozenHoneybeeWasEnabled;
            }

            _frozenHoneybee = null;
            _frozenHoneybeeBody = null;
            _frozenHoneybeeWasEnabled = false;
        }

        private static RectTransform FindAncestorByName(Transform start, string name)
        {
            Transform current = start;
            while (current != null)
            {
                if (current.name == name)
                {
                    return current as RectTransform;
                }
                current = current.parent;
            }
            return null;
        }

        private static bool IsCampaignMission(int missionId)
        {
            return ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                   ConfigData.UserProgressData != null &&
                   ConfigData.Configuration != null &&
                   ConfigData.UserProgressData.GetCurrentLevel(
                       ConfigData.Configuration.UserSide,
                       ConfigData.GameModes.Campaign) == missionId;
        }

        private void OnDestroy()
        {
            ReleaseHoneybee();
        }
    }
}
