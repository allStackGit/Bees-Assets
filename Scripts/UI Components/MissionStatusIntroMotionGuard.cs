using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Presents the mission objective in the middle of the screen long enough to be read, then
    /// slides the existing banner into its authored top-of-screen HUD position.
    /// </summary>
    [DefaultExecutionOrder(1500)]
    public sealed class MissionStatusIntroMotionGuard : MonoBehaviour
    {
        private const float HoldDuration = 2f;
        private const float SlideDuration = 0.45f;

        private GameMenus _menus;
        private RectTransform _statusRect;
        private Vector2 _startPosition;
        private Vector2 _targetPosition;
        private float _shownAt;
        private bool _hasStarted;
        private bool _hasFinished;
        private bool _wasActive;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                GameMenus[] menus = roots[rootIndex].GetComponentsInChildren<GameMenus>(true);
                for (int menuIndex = 0; menuIndex < menus.Length; menuIndex++)
                {
                    GameMenus menu = menus[menuIndex];
                    MissionStatusIntroMotionGuard guard =
                        menu.GetComponent<MissionStatusIntroMotionGuard>();
                    if (guard == null)
                    {
                        guard = menu.gameObject.AddComponent<MissionStatusIntroMotionGuard>();
                    }
                    guard.Initialize(menu);
                }
            }
        }

        private void Awake()
        {
            if (_menus == null)
            {
                Initialize(GetComponent<GameMenus>());
            }
        }

        private void Initialize(GameMenus menus)
        {
            _menus = menus;
            _statusRect = menus != null && menus.MissionStatus != null
                ? menus.MissionStatus.GetComponent<RectTransform>()
                : null;
            _hasStarted = false;
            _hasFinished = false;
            _wasActive = false;
        }

        private void LateUpdate()
        {
            if (_menus == null || _menus.MissionStatus == null || _statusRect == null)
            {
                return;
            }

            bool isActive = _menus.MissionStatus.activeInHierarchy;
            if (!isActive)
            {
                // If the serialized object was briefly active before mission setup hid it, re-arm
                // so the first real objective presentation still receives the animation.
                if (_hasStarted && !_hasFinished)
                {
                    _hasStarted = false;
                }
                _wasActive = false;
                return;
            }

            if (!_hasStarted && !_wasActive)
            {
                BeginPresentation();
            }
            _wasActive = true;

            if (!_hasStarted || _hasFinished)
            {
                return;
            }

            float elapsed = Time.unscaledTime - _shownAt;
            if (elapsed <= HoldDuration)
            {
                _statusRect.anchoredPosition = _startPosition;
                return;
            }

            float t = Mathf.Clamp01((elapsed - HoldDuration) / SlideDuration);
            t = Mathf.SmoothStep(0f, 1f, t);
            _statusRect.anchoredPosition = Vector2.LerpUnclamped(_startPosition, _targetPosition, t);
            if (t >= 1f)
            {
                _statusRect.anchoredPosition = _targetPosition;
                _hasFinished = true;
            }
        }

        private void BeginPresentation()
        {
            Canvas canvas = _statusRect.GetComponentInParent<Canvas>();
            RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            if (canvasRect == null || canvasRect.rect.height <= 0f)
            {
                return;
            }

            Vector2 anchorMin = _statusRect.anchorMin;
            Vector2 anchorMax = _statusRect.anchorMax;
            Vector2 pivot = _statusRect.pivot;
            anchorMin.y = 1f;
            anchorMax.y = 1f;
            pivot.y = 1f;
            _statusRect.anchorMin = anchorMin;
            _statusRect.anchorMax = anchorMax;
            _statusRect.pivot = pivot;

            _targetPosition = _statusRect.anchoredPosition;
            _targetPosition.y = 0f;
            float statusHeight = Mathf.Max(_statusRect.rect.height, _statusRect.sizeDelta.y);
            _startPosition = _targetPosition;
            _startPosition.y = -(canvasRect.rect.height * 0.5f - statusHeight * 0.5f);
            _statusRect.anchoredPosition = _startPosition;
            _shownAt = Time.unscaledTime;
            _hasStarted = true;
            _hasFinished = false;
        }
    }
}
