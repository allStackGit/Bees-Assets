using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Scenes
{
    [DefaultExecutionOrder(20000)]
    internal sealed class CampaignCameraFollowGuard : MonoBehaviour
    {
        private Stage _stage;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Campaign Camera Follow Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignCameraFollowGuard>();
        }

        private void LateUpdate()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            if (_stage == null)
            {
                _stage = FindObjectOfType<Stage>();
            }

            if (_stage == null || _stage.Camera == null || _stage.PrimaryLevel == null ||
                _stage.PrimaryLevel.Map == null || _stage.PrimaryLevel.Map.SpriteRenderer == null)
            {
                return;
            }

            Ship cameraShip = _stage.CameraShip;
            if (!_stage.IsFollowingShip && ShouldContinueScriptedFollow(cameraShip))
            {
                // Pluto I temporarily suspends following while the reveal owns the camera. Once the
                // scripted Scout starts leaving, resume following it through the intended handoff.
                _stage.IsFollowingShip = true;
            }

            if (_stage.IsFollowingShip && cameraShip != null && !cameraShip.IsDead)
            {
                Vector2 shipPosition = cameraShip.GetPosition();
                Vector3 currentPosition = _stage.Camera.transform.position;
                _stage.Camera.transform.position = new Vector3(shipPosition.x, shipPosition.y, currentPosition.z);
            }

            // The Scout can leave the playable map, but the campaign viewport must stop at its
            // edge. Apply this after follow/reveal positioning so no campaign camera owner can
            // expose the space outside the authored backdrop.
            ClampCameraToMap(_stage.Camera);
        }

        private bool ShouldContinueScriptedFollow(Ship cameraShip)
        {
            return _stage.IsPlayerControlling && IsScriptedScoutDeparture(cameraShip);
        }

        private static bool IsScriptedScoutDeparture(Ship cameraShip)
        {
            return cameraShip != null &&
                !cameraShip.IsDead &&
                cameraShip.CanOverrideBounds &&
                cameraShip.ShipType == ConfigData.ShipTypes.Scout;
        }

        private void ClampCameraToMap(Camera camera)
        {
            Bounds mapBounds = _stage.PrimaryLevel.Map.SpriteRenderer.bounds;
            if (camera.aspect <= 0f)
            {
                return;
            }

            // Position clamping cannot keep the viewport inside the map when the camera itself is
            // wider/taller than the map. Reduce the zoom first, then clamp its center point.
            float maximumVerticalSize = Mathf.Min(mapBounds.extents.y, mapBounds.extents.x / camera.aspect);
            if (maximumVerticalSize > 0f && camera.orthographicSize > maximumVerticalSize)
            {
                camera.orthographicSize = maximumVerticalSize;
            }

            float verticalExtent = camera.orthographicSize;
            float horizontalExtent = camera.aspect * verticalExtent;
            float minimumX = mapBounds.min.x + horizontalExtent;
            float maximumX = mapBounds.max.x - horizontalExtent;
            float minimumY = mapBounds.min.y + verticalExtent;
            float maximumY = mapBounds.max.y - verticalExtent;

            Vector3 position = camera.transform.position;
            float clampedX = minimumX <= maximumX
                ? Mathf.Clamp(position.x, minimumX, maximumX)
                : mapBounds.center.x;
            float clampedY = minimumY <= maximumY
                ? Mathf.Clamp(position.y, minimumY, maximumY)
                : mapBounds.center.y;

            camera.transform.position = new Vector3(clampedX, clampedY, position.z);
        }
    }
}
