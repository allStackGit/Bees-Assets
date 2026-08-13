using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Levels
{
    [DefaultExecutionOrder(-1000)]
    internal sealed class MouseWindowScrollGuard : MonoBehaviour
    {
        private Stage _stage;
        private bool _suppressed;
        private bool _savedStageMouseScrolling;
        private bool _savedUserMouseScrolling;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Stage stage = Object.FindObjectOfType<Stage>();
            if (stage == null || stage.gameObject.GetComponent<MouseWindowScrollGuard>() != null)
            {
                return;
            }

            MouseWindowScrollGuard guard = stage.gameObject.AddComponent<MouseWindowScrollGuard>();
            guard._stage = stage;
        }

        private void Update()
        {
            if (_stage == null || ConfigData.UserProgressData == null)
            {
                Restore();
                return;
            }

            Vector3 mouse = Input.mousePosition;
            bool insideWindow = Application.isFocused &&
                                mouse.x >= 0f && mouse.x < Screen.width &&
                                mouse.y >= 0f && mouse.y < Screen.height;
            if (insideWindow)
            {
                Restore();
                return;
            }

            if (!_suppressed)
            {
                _savedStageMouseScrolling = _stage.UseMouseScrolling;
                _savedUserMouseScrolling = ConfigData.UserProgressData.UseMouseScrolling;
                _suppressed = true;
            }

            _stage.UseMouseScrolling = false;
            ConfigData.UserProgressData.UseMouseScrolling = false;
        }

        private void Restore()
        {
            if (!_suppressed)
            {
                return;
            }

            if (_stage != null)
            {
                _stage.UseMouseScrolling = _savedStageMouseScrolling;
            }
            if (ConfigData.UserProgressData != null)
            {
                ConfigData.UserProgressData.UseMouseScrolling = _savedUserMouseScrolling;
            }
            _suppressed = false;
        }

        private void OnDisable()
        {
            Restore();
        }

        private void OnDestroy()
        {
            Restore();
        }
    }
}
