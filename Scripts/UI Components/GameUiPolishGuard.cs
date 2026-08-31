using System.Collections.Generic;
using System.Text;
using Assets.Scripts;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Owns small semantic HUD/summary presentation rules that should remain independent of the
    /// authored responsive geometry managed by GameHudLayoutGuard.
    /// </summary>
    public sealed class GameUiPolishGuard : MonoBehaviour
    {
        private const float MissionStatusFontScale = 1.25f;
        private const float MissionStatusMinHeight = 24f;
        private const float MissionStatusVerticalPadding = 3f;

        private GameMenus _menus;
        private bool _missionStatusStyled;
        private bool _summaryWasVisible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameMenus[] menus = roots[i].GetComponentsInChildren<GameMenus>(true);
                for (int j = 0; j < menus.Length; j++)
                {
                    GameMenus menu = menus[j];
                    GameUiPolishGuard guard = menu.GetComponent<GameUiPolishGuard>();
                    if (guard == null)
                    {
                        guard = menu.gameObject.AddComponent<GameUiPolishGuard>();
                    }
                    guard.Initialize(menu);
                }
            }
        }

        private void Awake()
        {
            if (_menus == null)
            {
                _menus = GetComponent<GameMenus>();
            }
        }

        private void Start()
        {
            ApplyMissionStatusStyle();
        }

        private void LateUpdate()
        {
            if (_menus == null)
            {
                return;
            }

            ApplyMissionStatusStyle();

            bool summaryVisible = _menus.SummaryPanel != null && _menus.SummaryPanel.activeInHierarchy;
            if (summaryVisible && !_summaryWasVisible)
            {
                ApplySummaryLabels();
            }
            _summaryWasVisible = summaryVisible;
        }

        private void Initialize(GameMenus menus)
        {
            _menus = menus;
            ApplyMissionStatusStyle();
        }

        private void ApplyMissionStatusStyle()
        {
            if (_missionStatusStyled || _menus == null || _menus.MissionStatusText == null)
            {
                return;
            }

            TMP_Text text = _menus.MissionStatusText;
            if (text.enableAutoSizing)
            {
                text.fontSizeMin *= MissionStatusFontScale;
                text.fontSizeMax *= MissionStatusFontScale;
            }
            else
            {
                text.fontSize *= MissionStatusFontScale;
            }

            RectTransform textRect = text.rectTransform;
            RectTransform statusRect = _menus.MissionStatus != null
                ? _menus.MissionStatus.GetComponent<RectTransform>()
                : null;

            if (statusRect != null)
            {
                float availableTextWidth = textRect != null
                    ? Mathf.Max(1f, textRect.rect.width)
                    : Mathf.Max(1f, statusRect.rect.width);
                float preferredTextHeight = text.GetPreferredValues("Ag", availableTextWidth, 0f).y;
                float statusHeight = CalculateMissionStatusHeight(preferredTextHeight);

                // The mission-status owner is already positioned at the top of the gameplay HUD.
                // Make the green banner itself begin at that top edge and grow downward, instead of
                // leaving the original half-height inset that lets the enlarged text protrude above it.
                Vector2 statusAnchorMin = statusRect.anchorMin;
                Vector2 statusAnchorMax = statusRect.anchorMax;
                Vector2 statusPivot = statusRect.pivot;
                Vector2 statusPosition = statusRect.anchoredPosition;
                Vector2 statusSize = statusRect.sizeDelta;
                statusAnchorMin.y = 1f;
                statusAnchorMax.y = 1f;
                statusPivot.y = 1f;
                statusPosition.y = 0f;
                statusSize.y = statusHeight;
                statusRect.anchorMin = statusAnchorMin;
                statusRect.anchorMax = statusAnchorMax;
                statusRect.pivot = statusPivot;
                statusRect.anchoredPosition = statusPosition;
                statusRect.sizeDelta = statusSize;

                // Some versions of the Space HUD contain one or more fixed-height wrappers between
                // Mission Status and its TMP label. Let those wrappers follow the enlarged banner
                // vertically while preserving every authored horizontal relationship.
                RectTransform current = textRect != null ? textRect.parent as RectTransform : null;
                while (current != null && current != statusRect)
                {
                    StretchVertically(current, 0f);
                    current = current.parent as RectTransform;
                }
            }

            if (textRect != null)
            {
                StretchVertically(textRect, MissionStatusVerticalPadding);
            }

            _missionStatusStyled = true;
        }

        internal static float CalculateMissionStatusHeight(float preferredTextHeight)
        {
            return Mathf.Max(
                MissionStatusMinHeight,
                Mathf.Max(0f, preferredTextHeight) + MissionStatusVerticalPadding * 2f);
        }

        private static void StretchVertically(RectTransform rect, float padding)
        {
            if (rect == null)
            {
                return;
            }

            Vector2 anchorMin = rect.anchorMin;
            Vector2 anchorMax = rect.anchorMax;
            Vector2 offsetMin = rect.offsetMin;
            Vector2 offsetMax = rect.offsetMax;
            anchorMin.y = 0f;
            anchorMax.y = 1f;
            offsetMin.y = Mathf.Max(0f, padding);
            offsetMax.y = -Mathf.Max(0f, padding);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private void ApplySummaryLabels()
        {
            if (_menus.Stage == null || _menus.Stage.PrimaryLevel == null || _menus.Stage.PrimaryLevel.State == null)
            {
                return;
            }

            Levels.GameState state = _menus.Stage.PrimaryLevel.State;
            if (_menus.ShipsDestroyedText != null)
            {
                _menus.ShipsDestroyedText.text = $"Enemy Ships Destroyed: {state.EnemyShipsDestroyedByPlayer}";
            }
            if (_menus.ShipsLostText != null)
            {
                _menus.ShipsLostText.text = BuildShipsLostText(state);
                _menus.ShipsLostText.enableWordWrapping = true;
            }
        }

        private static string BuildShipsLostText(Levels.GameState state)
        {
            if (state.PlayerShipsLostByType.Count == 0)
            {
                return $"Ships Lost: {state.PlayerShipsLost}";
            }

            List<KeyValuePair<ConfigData.ShipTypes, int>> losses =
                new List<KeyValuePair<ConfigData.ShipTypes, int>>(state.PlayerShipsLostByType);
            losses.Sort((left, right) => string.CompareOrdinal(Nicify(left.Key.ToString()), Nicify(right.Key.ToString())));

            StringBuilder text = new StringBuilder();
            text.Append("Ships Lost: ").Append(state.PlayerShipsLost).Append(" (");
            for (int i = 0; i < losses.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }
                text.Append(Nicify(losses[i].Key.ToString())).Append(" x").Append(losses[i].Value);
            }
            text.Append(')');
            return text.ToString();
        }

        private static string Nicify(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            StringBuilder result = new StringBuilder(value.Length + 4);
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (i > 0 && char.IsUpper(current) && char.IsLower(value[i - 1]))
                {
                    result.Append(' ');
                }
                result.Append(current);
            }
            return result.ToString();
        }
    }
}
