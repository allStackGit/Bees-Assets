using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Adds list-to-list/list-to-editor drag and drop to Squad Maker and keeps the START/TEST
    /// hover descriptions in an unclipped root-canvas overlay.
    ///
    /// Existing click/double-click behavior remains the non-drag path. Squad loading/choosing still
    /// goes through SquadMaker's existing confirmation and validation methods.
    /// </summary>
    [DefaultExecutionOrder(-650)]
    public sealed class SquadMakerInteractionGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string SavedSquadPrefix = "Saved Squad - ";
        private const string ChosenSquadPrefix = "Chosen Squad - ";
        private const string SquadMakerColumnName = "Squad Maker Column";
        private const string HoverOverlayName = "Squad Maker Hover Text Overlay";
        private const string DragPreviewName = "Squad Drag Preview";
        private const float ScanInterval = 0.20f;
        private const float HoverGap = 8f;
        private const float HoverMargin = 8f;

        private static readonly FieldInfo SquadToLoadField = typeof(SquadMaker).GetField(
            "_squadToLoad",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SquadToChooseField = typeof(SquadMaker).GetField(
            "_squadToChoose",
            BindingFlags.Instance | BindingFlags.NonPublic);

        internal enum SquadListSource
        {
            Saved,
            Chosen
        }

        internal enum SquadDropTarget
        {
            None,
            SavedList,
            ChosenList,
            Editor
        }

        internal enum SquadDropAction
        {
            None,
            Load,
            Choose,
            Unchoose,
            UnchooseAndLoad
        }

        private SquadMaker _squadMaker;
        private Canvas _rootCanvas;
        private RectTransform _rootCanvasRect;
        private RectTransform _savedListDropRect;
        private RectTransform _chosenListDropRect;
        private RectTransform _editorDropRect;
        private RectTransform _hoverOverlay;
        private GameObject _dragPreview;
        private float _nextScanTime;
        private int _pendingUnchooseAfterLoadId = int.MinValue;
        private bool _pendingLoadDialogueWasOpen;
        private bool _warnedMissingReflectionFields;

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

                SquadMakerInteractionGuard guard =
                    squadMaker.GetComponent<SquadMakerInteractionGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerInteractionGuard>();
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
            ResolveDropSurfaces();
            _nextScanTime = 0f;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_rootCanvas == null || _rootCanvasRect == null)
            {
                ResolveDropSurfaces();
            }

            EnsureHoverOverlay();
            PositionHoverDescription(_squadMaker.StartButton, _squadMaker.StartText);
            PositionHoverDescription(_squadMaker.TestButton, _squadMaker.TestText);
            ResolvePendingUnchooseAfterLoad();

            if (Time.unscaledTime >= _nextScanTime)
            {
                _nextScanTime = Time.unscaledTime + ScanInterval;
                AttachHandlesToDynamicRows();
            }
        }

        private void OnDisable()
        {
            DestroyDragPreview();
            ClearPendingUnchooseAfterLoad();
        }

        private void ResolveDropSurfaces()
        {
            _rootCanvas = ResolveRootCanvas(_squadMaker);
            _rootCanvasRect = _rootCanvas != null ? _rootCanvas.transform as RectTransform : null;
            _savedListDropRect = ResolveListViewport(_squadMaker != null ? _squadMaker.SavedSquadList : null);
            _chosenListDropRect = ResolveListViewport(_squadMaker != null ? _squadMaker.ChosenSquadList : null);

            RectTransform dropZone = _squadMaker != null && _squadMaker.DropZone != null
                ? _squadMaker.DropZone.transform as RectTransform
                : null;
            _editorDropRect = FindAncestorByName(dropZone, SquadMakerColumnName);
            if (_editorDropRect == null)
            {
                _editorDropRect = dropZone;
            }
        }

        private void AttachHandlesToDynamicRows()
        {
            AttachHandles(_squadMaker.SavedSquadList, SquadListSource.Saved, SavedSquadPrefix);
            AttachHandles(_squadMaker.ChosenSquadList, SquadListSource.Chosen, ChosenSquadPrefix);
        }

        private void AttachHandles(GameObject list, SquadListSource source, string prefix)
        {
            if (list == null)
            {
                return;
            }

            Transform listTransform = list.transform;
            for (int index = 0; index < listTransform.childCount; index++)
            {
                Transform child = listTransform.GetChild(index);
                if (child == null || !child.gameObject.activeInHierarchy ||
                    !child.name.StartsWith(prefix, StringComparison.Ordinal) ||
                    !TryParseSquadId(child.name, out int squadId))
                {
                    continue;
                }

                SquadMakerSquadRowDragHandle handle =
                    child.GetComponent<SquadMakerSquadRowDragHandle>();
                if (handle == null)
                {
                    handle = child.gameObject.AddComponent<SquadMakerSquadRowDragHandle>();
                }

                handle.Configure(this, source, squadId);
            }
        }

        internal void BeginRowDrag(SquadMakerSquadRowDragHandle handle, PointerEventData eventData)
        {
            if (handle == null || eventData == null || IsModalOpen())
            {
                return;
            }

            ResolveDropSurfaces();
            DestroyDragPreview();
            if (_rootCanvasRect == null)
            {
                return;
            }

            _dragPreview = Instantiate(handle.gameObject, _rootCanvasRect, false);
            _dragPreview.name = DragPreviewName;
            _dragPreview.transform.SetAsLastSibling();

            SquadMakerSquadRowDragHandle[] clonedHandles =
                _dragPreview.GetComponentsInChildren<SquadMakerSquadRowDragHandle>(true);
            for (int index = 0; index < clonedHandles.Length; index++)
            {
                clonedHandles[index].enabled = false;
            }

            Graphic[] graphics = _dragPreview.GetComponentsInChildren<Graphic>(true);
            for (int index = 0; index < graphics.Length; index++)
            {
                graphics[index].raycastTarget = false;
            }

            CanvasGroup group = _dragPreview.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = _dragPreview.AddComponent<CanvasGroup>();
            }
            group.alpha = 0.80f;
            group.interactable = false;
            group.blocksRaycasts = false;

            RectTransform previewRect = _dragPreview.transform as RectTransform;
            if (previewRect != null)
            {
                RectTransform sourceRect = handle.transform as RectTransform;
                Vector2 sourceSize = sourceRect != null
                    ? sourceRect.rect.size
                    : previewRect.rect.size;
                previewRect.anchorMin = new Vector2(0.5f, 0.5f);
                previewRect.anchorMax = new Vector2(0.5f, 0.5f);
                previewRect.pivot = new Vector2(0.5f, 0.5f);
                previewRect.sizeDelta = sourceSize;
                previewRect.localScale = Vector3.one;
            }

            MoveDragPreview(eventData.position);
        }

        internal void UpdateRowDrag(SquadMakerSquadRowDragHandle handle, PointerEventData eventData)
        {
            if (_dragPreview == null || eventData == null)
            {
                return;
            }

            MoveDragPreview(eventData.position);
        }

        internal void EndRowDrag(SquadMakerSquadRowDragHandle handle, PointerEventData eventData)
        {
            try
            {
                if (handle == null || eventData == null || IsModalOpen())
                {
                    return;
                }

                SquadDropTarget target = ResolveDropTarget(eventData.position);
                SquadDropAction action = ResolveDropAction(handle.Source, target);
                ExecuteDropAction(action, handle.SquadId, handle.gameObject);
            }
            finally
            {
                DestroyDragPreview();
            }
        }

        private void MoveDragPreview(Vector2 screenPosition)
        {
            RectTransform previewRect = _dragPreview != null
                ? _dragPreview.transform as RectTransform
                : null;
            if (previewRect == null || _rootCanvasRect == null)
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rootCanvasRect,
                screenPosition,
                EventCamera,
                out Vector2 localPoint))
            {
                previewRect.anchoredPosition = localPoint;
            }
        }

        private SquadDropTarget ResolveDropTarget(Vector2 screenPosition)
        {
            if (ContainsScreenPoint(_savedListDropRect, screenPosition))
            {
                return SquadDropTarget.SavedList;
            }
            if (ContainsScreenPoint(_chosenListDropRect, screenPosition))
            {
                return SquadDropTarget.ChosenList;
            }
            if (ContainsScreenPoint(_editorDropRect, screenPosition))
            {
                return SquadDropTarget.Editor;
            }
            return SquadDropTarget.None;
        }

        private bool ContainsScreenPoint(RectTransform rect, Vector2 screenPosition)
        {
            return rect != null &&
                rect.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, EventCamera);
        }

        private void ExecuteDropAction(SquadDropAction action, int squadId, GameObject row)
        {
            SavedSquad squad = GetSavedSquad(squadId);
            if (squad == null)
            {
                return;
            }

            switch (action)
            {
                case SquadDropAction.Load:
                    RequestLoadSquad(squad);
                    break;
                case SquadDropAction.Choose:
                    RequestChooseSquad(squad);
                    break;
                case SquadDropAction.Unchoose:
                    if (row != null)
                    {
                        _squadMaker.ConfirmUnchooseSquad(row);
                    }
                    break;
                case SquadDropAction.UnchooseAndLoad:
                    RequestUnchooseAndLoad(squad, row);
                    break;
            }
        }

        private SavedSquad GetSavedSquad(int squadId)
        {
            return ConfigData.CurrentShips != null
                ? ConfigData.CurrentShips.GetSavedSquad(squadId)
                : null;
        }

        private void RequestLoadSquad(SavedSquad squad)
        {
            if (squad == null || _squadMaker == null)
            {
                return;
            }

            SavedSquad current = _squadMaker.GetCurrentSquad();
            if (current != null && current.Id == squad.Id)
            {
                // Dragging the squad already being edited back into the editor must not discard
                // unsaved edits by re-cloning the persistent squad over the working copy.
                return;
            }

            if (!TrySetPendingSquad(SquadToLoadField, squad))
            {
                return;
            }

            _squadMaker.ConfirmLoadSquad();
        }

        private void RequestChooseSquad(SavedSquad squad)
        {
            if (squad == null || _squadMaker == null ||
                !TrySetPendingSquad(SquadToChooseField, squad))
            {
                return;
            }

            _squadMaker.ConfirmChooseSquad();
        }

        private void RequestUnchooseAndLoad(SavedSquad squad, GameObject chosenRow)
        {
            if (squad == null || _squadMaker == null || chosenRow == null)
            {
                return;
            }

            SavedSquad current = _squadMaker.GetCurrentSquad();
            if (current != null && current.Id == squad.Id)
            {
                // Keep the live working copy intact while simply removing the squad from the level.
                _squadMaker.ConfirmUnchooseSquad(chosenRow);
                return;
            }

            if (!TrySetPendingSquad(SquadToLoadField, squad))
            {
                return;
            }

            _pendingUnchooseAfterLoadId = squad.Id;
            _pendingLoadDialogueWasOpen = false;
            _squadMaker.ConfirmLoadSquad();

            current = _squadMaker.GetCurrentSquad();
            if (current != null && current.Id == squad.Id)
            {
                UnchoosePendingSquad();
            }
            else if (_squadMaker.LoadSquadConfirmation != null &&
                     _squadMaker.LoadSquadConfirmation.IsOpen)
            {
                _pendingLoadDialogueWasOpen = true;
            }
            else
            {
                ClearPendingUnchooseAfterLoad();
            }
        }

        private bool TrySetPendingSquad(FieldInfo field, SavedSquad squad)
        {
            if (field == null)
            {
                if (!_warnedMissingReflectionFields)
                {
                    _warnedMissingReflectionFields = true;
                    Debug.LogError(
                        "Squad Maker drag/drop could not resolve its existing pending-squad fields; " +
                        "drag actions are disabled rather than bypassing normal validation.");
                }
                return false;
            }

            field.SetValue(_squadMaker, squad);
            return true;
        }

        private void ResolvePendingUnchooseAfterLoad()
        {
            if (_pendingUnchooseAfterLoadId == int.MinValue || _squadMaker == null)
            {
                return;
            }

            SavedSquad current = _squadMaker.GetCurrentSquad();
            if (current != null && current.Id == _pendingUnchooseAfterLoadId)
            {
                UnchoosePendingSquad();
                return;
            }

            if (_pendingLoadDialogueWasOpen &&
                (_squadMaker.LoadSquadConfirmation == null ||
                 !_squadMaker.LoadSquadConfirmation.IsOpen))
            {
                // The modal closed without loading the requested squad (normally the user chose No).
                ClearPendingUnchooseAfterLoad();
            }
        }

        private void UnchoosePendingSquad()
        {
            if (_pendingUnchooseAfterLoadId == int.MinValue || _squadMaker == null)
            {
                return;
            }

            GameObject chosenRow = GameObject.Find(
                FindRowNameById(_squadMaker.ChosenSquadList, ChosenSquadPrefix, _pendingUnchooseAfterLoadId));
            if (chosenRow != null)
            {
                _squadMaker.ConfirmUnchooseSquad(chosenRow);
            }
            ClearPendingUnchooseAfterLoad();
        }

        private void ClearPendingUnchooseAfterLoad()
        {
            _pendingUnchooseAfterLoadId = int.MinValue;
            _pendingLoadDialogueWasOpen = false;
        }

        private bool IsModalOpen()
        {
            return (_squadMaker.LoadSquadConfirmation != null && _squadMaker.LoadSquadConfirmation.IsOpen) ||
                (_squadMaker.ChooseSquadConfirmation != null && _squadMaker.ChooseSquadConfirmation.IsOpen) ||
                (_squadMaker.UnchooseSquadConfirmation != null && _squadMaker.UnchooseSquadConfirmation.IsOpen);
        }

        private void DestroyDragPreview()
        {
            if (_dragPreview == null)
            {
                return;
            }

            Destroy(_dragPreview);
            _dragPreview = null;
        }

        private void EnsureHoverOverlay()
        {
            if (_hoverOverlay != null || _rootCanvasRect == null || _squadMaker == null)
            {
                return;
            }

            Transform existing = _rootCanvasRect.Find(HoverOverlayName);
            _hoverOverlay = existing as RectTransform;
            if (_hoverOverlay == null)
            {
                GameObject overlayObject = new GameObject(HoverOverlayName, typeof(RectTransform));
                _hoverOverlay = overlayObject.GetComponent<RectTransform>();
                _hoverOverlay.SetParent(_rootCanvasRect, false);
            }

            _hoverOverlay.anchorMin = Vector2.zero;
            _hoverOverlay.anchorMax = Vector2.one;
            _hoverOverlay.pivot = new Vector2(0.5f, 0.5f);
            _hoverOverlay.anchoredPosition = Vector2.zero;
            _hoverOverlay.sizeDelta = Vector2.zero;
            _hoverOverlay.localScale = Vector3.one;
            _hoverOverlay.SetAsLastSibling();

            MoveDescriptionToOverlay(_squadMaker.StartText);
            MoveDescriptionToOverlay(_squadMaker.TestText);
        }

        private void MoveDescriptionToOverlay(GameObject description)
        {
            RectTransform rect = description != null ? description.transform as RectTransform : null;
            if (rect == null || _hoverOverlay == null || rect.parent == _hoverOverlay)
            {
                return;
            }

            Vector2 authoredSize = rect.rect.size;
            if (authoredSize.x <= 0.01f || authoredSize.y <= 0.01f)
            {
                authoredSize = new Vector2(
                    Mathf.Abs(rect.sizeDelta.x),
                    Mathf.Abs(rect.sizeDelta.y));
            }

            rect.SetParent(_hoverOverlay, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = authoredSize;
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();
        }

        private void PositionHoverDescription(GameObject buttonObject, GameObject descriptionObject)
        {
            RectTransform button = buttonObject != null ? buttonObject.transform as RectTransform : null;
            RectTransform description = descriptionObject != null
                ? descriptionObject.transform as RectTransform
                : null;
            if (button == null || description == null || _hoverOverlay == null ||
                description.parent != _hoverOverlay)
            {
                return;
            }

            Rect buttonRect = GetRectInLocalSpace(button, _hoverOverlay);
            Vector2 descriptionSize = description.rect.size;
            description.anchoredPosition = CalculateHoverDescriptionPosition(
                buttonRect,
                descriptionSize,
                _hoverOverlay.rect,
                HoverGap,
                HoverMargin);
        }

        internal static Vector2 CalculateHoverDescriptionPosition(
            Rect buttonRect,
            Vector2 descriptionSize,
            Rect overlayRect,
            float gap = HoverGap,
            float margin = HoverMargin)
        {
            float width = Mathf.Min(Mathf.Abs(descriptionSize.x),
                Mathf.Max(0f, overlayRect.width - margin * 2f));
            float height = Mathf.Min(Mathf.Abs(descriptionSize.y),
                Mathf.Max(0f, overlayRect.height - margin * 2f));
            float halfWidth = width * 0.5f;

            float x = Mathf.Clamp(
                buttonRect.center.x,
                overlayRect.xMin + margin + halfWidth,
                overlayRect.xMax - margin - halfWidth);

            float aboveY = buttonRect.yMax + gap;
            float maximumBottom = overlayRect.yMax - margin - height;
            float minimumBottom = overlayRect.yMin + margin;
            float y;
            if (aboveY <= maximumBottom)
            {
                y = Mathf.Max(minimumBottom, aboveY);
            }
            else
            {
                float belowY = buttonRect.yMin - gap - height;
                y = Mathf.Clamp(belowY, minimumBottom, maximumBottom);
            }

            return new Vector2(x, y);
        }

        internal static SquadDropAction ResolveDropAction(
            SquadListSource source,
            SquadDropTarget target)
        {
            if (source == SquadListSource.Saved)
            {
                switch (target)
                {
                    case SquadDropTarget.Editor:
                        return SquadDropAction.Load;
                    case SquadDropTarget.ChosenList:
                        return SquadDropAction.Choose;
                    default:
                        return SquadDropAction.None;
                }
            }

            switch (target)
            {
                case SquadDropTarget.Editor:
                    return SquadDropAction.UnchooseAndLoad;
                case SquadDropTarget.SavedList:
                    return SquadDropAction.Unchoose;
                default:
                    return SquadDropAction.None;
            }
        }

        internal static bool TryParseSquadId(string rowName, out int squadId)
        {
            squadId = 0;
            if (string.IsNullOrEmpty(rowName))
            {
                return false;
            }

            int hashIndex = rowName.LastIndexOf('#');
            return hashIndex >= 0 && hashIndex < rowName.Length - 1 &&
                int.TryParse(rowName.Substring(hashIndex + 1), out squadId);
        }

        private static string FindRowNameById(GameObject list, string prefix, int squadId)
        {
            if (list == null)
            {
                return string.Empty;
            }

            Transform listTransform = list.transform;
            for (int index = 0; index < listTransform.childCount; index++)
            {
                Transform child = listTransform.GetChild(index);
                if (child != null &&
                    child.name.StartsWith(prefix, StringComparison.Ordinal) &&
                    TryParseSquadId(child.name, out int childId) &&
                    childId == squadId)
                {
                    return child.name;
                }
            }
            return string.Empty;
        }

        private static Canvas ResolveRootCanvas(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return null;
            }

            Canvas canvas = squadMaker.ChosenSquadList != null
                ? squadMaker.ChosenSquadList.GetComponentInParent<Canvas>()
                : null;
            if (canvas == null && squadMaker.SavedSquadList != null)
            {
                canvas = squadMaker.SavedSquadList.GetComponentInParent<Canvas>();
            }
            return canvas != null ? canvas.rootCanvas : null;
        }

        private static RectTransform ResolveListViewport(GameObject list)
        {
            if (list == null)
            {
                return null;
            }

            ScrollRect scroll = list.GetComponentInParent<ScrollRect>();
            if (scroll != null && scroll.viewport != null)
            {
                return scroll.viewport;
            }
            return list.transform as RectTransform;
        }

        private static RectTransform FindAncestorByName(RectTransform start, string name)
        {
            Transform current = start;
            while (current != null)
            {
                if (current.name == name)
                {
                    return current as RectTransform;
                }
                current = current.parent;
            }
            return null;
        }

        private static Rect GetRectInLocalSpace(RectTransform rect, RectTransform localOwner)
        {
            if (rect == null || localOwner == null)
            {
                return default(Rect);
            }

            Vector3[] worldCorners = new Vector3[4];
            rect.GetWorldCorners(worldCorners);
            Vector3 bottomLeft = localOwner.InverseTransformPoint(worldCorners[0]);
            Vector3 topRight = localOwner.InverseTransformPoint(worldCorners[2]);
            return Rect.MinMaxRect(bottomLeft.x, bottomLeft.y, topRight.x, topRight.y);
        }

        private Camera EventCamera
        {
            get
            {
                if (_rootCanvas == null || _rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    return null;
                }
                return _rootCanvas.worldCamera != null
                    ? _rootCanvas.worldCamera
                    : (_squadMaker != null ? _squadMaker.Camera : null);
            }
        }
    }

    public sealed class SquadMakerSquadRowDragHandle : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private SquadMakerInteractionGuard _owner;
        private SquadMakerInteractionGuard.SquadListSource _source;
        private int _squadId;

        internal SquadMakerInteractionGuard.SquadListSource Source => _source;
        internal int SquadId => _squadId;

        internal void Configure(
            SquadMakerInteractionGuard owner,
            SquadMakerInteractionGuard.SquadListSource source,
            int squadId)
        {
            _owner = owner;
            _source = source;
            _squadId = squadId;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _owner?.BeginRowDrag(this, eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            _owner?.UpdateRowDrag(this, eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _owner?.EndRowDrag(this, eventData);
        }
    }
}
