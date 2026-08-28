using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps Squad Maker display resizing presentation-only.
    ///
    /// SquadMaker's legacy UpdateDimensions callback clears and rebuilds the current SquadShips
    /// when Screen.width/height changes. That makes display resizing behave like gameplay input.
    /// This guard replaces only that resize path: it keeps the legacy screen metrics/color-picker
    /// refresh, then reprojects existing canonical offsets into the fixed drag workspace without
    /// changing squad membership or SquadShip.Offset.
    /// </summary>
    [DefaultExecutionOrder(900)]
    public sealed class SquadMakerDragWorkspaceResizeGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string LegacyResizeCallback = "UpdateDimensions";
        private const float CornerTolerance = 0.001f;

        private readonly Vector3[] _currentCorners = new Vector3[4];
        private readonly Vector3[] _lastCorners = new Vector3[4];

        private SquadMaker _squadMaker;
        private Dropper _dropper;
        private RectTransform _dropZone;
        private bool _hasPresentationSnapshot;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SquadMakerSceneName)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                SquadMaker squadMaker = root.GetComponentInChildren<SquadMaker>(true);
                if (squadMaker == null)
                {
                    continue;
                }

                SquadMakerDragWorkspaceResizeGuard guard =
                    squadMaker.GetComponent<SquadMakerDragWorkspaceResizeGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerDragWorkspaceResizeGuard>();
                }

                guard.Initialize(squadMaker);
                return;
            }
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                Initialize(GetComponent<SquadMaker>());
            }
        }

        private void Initialize(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return;
            }

            _squadMaker = squadMaker;
            _dropper = null;
            _dropZone = null;
            _hasPresentationSnapshot = false;
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            // SquadMaker.Start still schedules the legacy method. Suppress that one semantic resize
            // path, but explicitly preserve its non-gameplay screen-metric responsibilities below.
            if (_squadMaker.IsInvoking(LegacyResizeCallback))
            {
                _squadMaker.CancelInvoke(LegacyResizeCallback);
            }

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                RefreshDisplayMetrics();
            }

            Dropper dropper = _squadMaker.GetDropper();
            RectTransform dropZone = _squadMaker.DropZone != null
                ? _squadMaker.DropZone.transform as RectTransform
                : null;
            if (dropper == null || dropZone == null)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                return;
            }

            bool ownerChanged = _dropper != dropper || _dropZone != dropZone;
            bool geometryChanged = ownerChanged || HasDropZoneGeometryChanged(dropZone);

            if (!ownerChanged && !displayChanged && !geometryChanged)
            {
                return;
            }

            _dropper = dropper;
            _dropZone = dropZone;
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;

            // This is deliberately the only drag-workspace resize side effect. It changes rendered
            // positions/scales only; Dropper.RefreshWorkspacePresentation never performs a drop.
            _dropper.RefreshWorkspacePresentation();
            CaptureDropZoneGeometry(dropZone);
        }

        private void RefreshDisplayMetrics()
        {
            ConfigData.ScreenWidth = Screen.width;
            ConfigData.ScreenHeight = Screen.height;

            Vector2 reference = _squadMaker.ReferenceScreenSize;
            if (reference.x > 0.001f && reference.y > 0.001f)
            {
                _squadMaker.ScreenScaleFactor = new Vector2(
                    ConfigData.ScreenWidth / reference.x,
                    ConfigData.ScreenHeight / reference.y);
            }

            if (_squadMaker.HasColorPicker && _squadMaker.ColorPicker != null)
            {
                ColorPicker picker = _squadMaker.ColorPicker.GetComponent<ColorPicker>();
                if (picker != null)
                {
                    picker.SetScreenScaleFactor();
                }
            }
        }

        private bool HasDropZoneGeometryChanged(RectTransform dropZone)
        {
            if (!_hasPresentationSnapshot)
            {
                return true;
            }

            dropZone.GetWorldCorners(_currentCorners);
            for (int i = 0; i < _currentCorners.Length; i++)
            {
                if ((_currentCorners[i] - _lastCorners[i]).sqrMagnitude > CornerTolerance * CornerTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private void CaptureDropZoneGeometry(RectTransform dropZone)
        {
            dropZone.GetWorldCorners(_lastCorners);
            _hasPresentationSnapshot = true;
        }
    }
}
