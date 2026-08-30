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
                text.fontSizeMin *= 1.18f;
                text.fontSizeMax *= 1.18f;
            }
            else
            {
                text.fontSize *= 1.18f;
            }

            RectTransform rect = text.rectTransform;
            if (rect != null)
            {
                Vector2 size = rect.sizeDelta;
                size.y += 8f;
                rect.sizeDelta = size;
            }
            _missionStatusStyled = true;
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
