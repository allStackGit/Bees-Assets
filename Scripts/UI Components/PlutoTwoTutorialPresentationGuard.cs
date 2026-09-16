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

            Stage stage = FindObjectOfType<Stage>();
            if (stage == null || stage.Menus == null)
            {
                return;
            }

            CenterMissionStatus(stage);
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
    }
}
