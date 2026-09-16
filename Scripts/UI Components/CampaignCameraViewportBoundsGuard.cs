using Assets.Scripts;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Applies the existing camera boundary clamp after scripted follow/target movement and also
    /// limits orthographic size so the viewport itself can fit inside the map. MaintainScrollBoundary
    /// can only clamp position correctly when the viewport is not larger than the map.
    /// </summary>
    [DefaultExecutionOrder(40000)]
    internal sealed class CampaignCameraViewportBoundsGuard : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Campaign Camera Viewport Bounds Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignCameraViewportBoundsGuard>();
        }

        private void LateUpdate()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            Stage stage = FindObjectOfType<Stage>();
            if (stage == null || stage.Camera == null || stage.InputManager == null ||
                stage.PrimaryLevel == null || stage.PrimaryLevel.Map == null ||
                stage.PrimaryLevel.Map.SpriteRenderer == null)
            {
                return;
            }

            if (!stage.IsFollowingShip && !stage.IsCameraMovingToTarget)
            {
                return;
            }

            Camera camera = stage.Camera;
            Bounds mapBounds = stage.PrimaryLevel.Map.SpriteRenderer.bounds;
            float aspect = Mathf.Max(0.0001f, camera.aspect);
            float maximumOrthographicSize = Mathf.Min(
                mapBounds.extents.y,
                mapBounds.extents.x / aspect);

            if (maximumOrthographicSize > 0f && camera.orthographicSize > maximumOrthographicSize)
            {
                camera.orthographicSize = maximumOrthographicSize;
            }

            stage.InputManager.MaintainScrollBoundary();
        }
    }
}
