using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps Squad Maker display resizing presentation-only.
    ///
    /// SquadMaker's legacy UpdateDimensions callback rebuilt the current squad by clearing its
    /// SquadShips and semantically re-dropping every icon when Screen.width/height changed. That
    /// made persistent formation offsets depend on display timing and allowed resize to behave like
    /// gameplay input. The fixed drag workspace owns presentation instead: resizing may move/scale
    /// the rendered 600x340 workspace, but never changes squad membership or SquadShip.Offset.
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

            // SquadMaker.Start schedules this callback one second later. LateUpdate runs in the
            // first rendered frame after Start, so canceling here prevents the first semantic resize
            // pass and also protects against any future code that schedules it again.
            _squadMaker.CancelInvoke(LegacyResizeCallback);

            Dropper dropper = _squadMaker.GetDropper();
            RectTransform dropZone = _squadMaker.DropZone != null
                ? _squadMaker.DropZone.transform as RectTransform
                : null;
            if (dropper == null || dropZone == null)
            {
                return;
            }

            bool ownerChanged = _dropper != dropper || _dropZone != dropZone;
            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            bool geometryChanged = ownerChanged || HasDropZoneGeometryChanged(dropZone);

            if (!ownerChanged && !displayChanged && !geometryChanged)
            {
                return;
            }

            _dropper = dropper;
            _dropZone = dropZone;
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;

            // This is deliberately the only resize-side effect. It reprojects existing canonical
            // world offsets into the current presentation without adding/removing/repositioning any
            // SquadShip semantically.
            _dropper.RefreshWorkspacePresentation();
            CaptureDropZoneGeometry(dropZone);
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
