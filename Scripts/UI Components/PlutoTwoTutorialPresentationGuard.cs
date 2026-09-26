using Assets.Scripts;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps the Pluto II mission-status banner horizontally centered. Tutorial placement,
    /// highlight sizing, and dialogue sequencing are owned by the mission itself so they are
    /// correct before the frame is rendered rather than repaired afterward.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class PlutoTwoTutorialPresentationGuard : MonoBehaviour
    {
        private readonly Vector3[] _statusCorners = new Vector3[4];
        private readonly Vector3[] _canvasCorners = new Vector3[4];
        private Stage _stage;

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
                return;
            }

            if (_stage == null)
            {
                _stage = FindObjectOfType<Stage>();
            }
            if (_stage == null || _stage.Menus == null)
            {
                return;
            }

            CenterMissionStatus(_stage, _statusCorners, _canvasCorners);
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

        private static void CenterMissionStatus(Stage stage, Vector3[] statusCorners, Vector3[] canvasCorners)
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

            statusRect.GetWorldCorners(statusCorners);
            canvasRect.GetWorldCorners(canvasCorners);
            float statusCenterX = (statusCorners[0].x + statusCorners[2].x) * 0.5f;
            float canvasCenterX = (canvasCorners[0].x + canvasCorners[2].x) * 0.5f;
            Vector3 position = statusRect.position;
            position.x += canvasCenterX - statusCenterX;
            statusRect.position = position;
        }
    }
}
