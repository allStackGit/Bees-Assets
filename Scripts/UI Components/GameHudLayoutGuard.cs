using UnityEngine;
using UnityEngine.SceneManagement;
using Assets.Scripts.UIComponents;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps optional top-level HUD controls from occupying the same screen space.
    /// Campaign missions turn the clock on and off dynamically, while some legacy mission
    /// code also moves the speed button to fixed coordinates. Centralize the final layout
    /// here so a visible clock always owns its space.
    /// </summary>
    public sealed class GameHudLayoutGuard : MonoBehaviour
    {
        private const float ControlGap = 10f;

        private GameMenus _menus;
        private RectTransform _clockRect;
        private RectTransform _speedRect;
        private Vector2 _normalSpeedPosition;
        private bool _clockWasVisible;
        private bool _initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            GameMenus menus = Object.FindObjectOfType<GameMenus>();
            if (menus == null || menus.gameObject.GetComponent<GameHudLayoutGuard>() != null)
            {
                return;
            }

            GameHudLayoutGuard guard = menus.gameObject.AddComponent<GameHudLayoutGuard>();
            guard.Initialize(menus);
        }

        private void Initialize(GameMenus menus)
        {
            _menus = menus;
            if (_menus.Clock == null || _menus.GameSpeedButton == null)
            {
                enabled = false;
                return;
            }

            _clockRect = _menus.Clock.GetComponent<RectTransform>();
            _speedRect = _menus.GameSpeedButton.GetComponent<RectTransform>();
            if (_clockRect == null || _speedRect == null)
            {
                enabled = false;
                return;
            }

            _normalSpeedPosition = _speedRect.anchoredPosition;
            _clockWasVisible = false;
            _initialized = true;
            ApplyLayout();
        }

        private void LateUpdate()
        {
            if (!_initialized || _menus == null || _menus.Clock == null || _menus.GameSpeedButton == null)
            {
                return;
            }

            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible != _clockWasVisible || clockVisible)
            {
                ApplyLayout();
            }
        }

        private void ApplyLayout()
        {
            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible)
            {
                // Both controls are authored in the same HUD coordinate space. Put the speed
                // button immediately to the left of the clock, accounting for both widths.
                float x = _clockRect.anchoredPosition.x -
                          ((_clockRect.rect.width + _speedRect.rect.width) * 0.5f) - ControlGap;
                _speedRect.anchoredPosition = new Vector2(x, _clockRect.anchoredPosition.y);
            }
            else if (_clockWasVisible)
            {
                _speedRect.anchoredPosition = _normalSpeedPosition;
            }

            _clockWasVisible = clockVisible;
        }
    }
}
