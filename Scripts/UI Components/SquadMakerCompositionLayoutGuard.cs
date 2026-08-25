using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Captures and applies responsive relationships inside the Squad Maker's authored subregions.
    ///
    /// Scene geometry is immutable reference data, not a set of runtime pixel coordinates. Native
    /// LayoutGroups retain ownership of their children. Manual horizontal regions retain their authored
    /// fractions, the real Drop Zone stretches with the work surface, Formations belongs wholly inside
    /// the composition's left edge, the action buttons form one compact bottom row, and nested column
    /// wrappers/lists inherit the live cross-axis width of their structural column.
    ///
    /// SquadMakerResponsiveLayoutGuard remains the lifecycle owner and invokes this helper from its
    /// immutable reference snapshot. The color-picker relay owns only the picker overlay itself so that
    /// it can be positioned before its end-of-frame texture capture when the picker is activated.
    /// </summary>
    internal static class SquadMakerCompositionLayoutGuard
    {
        private const string FormationsName = "Formations";
        private const string LowerButtonsName = "Lower Buttons";
        private const string MainContainerName = "Main Container";
        private const string ShipSelectorColumnName = "Ship Selector Column";
        private const string SquadsColumnName = "Squads Column";
        private const string SavedSquadsColumnName = "Saved Squads Column";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const float GeometryTolerance = 0.001f;
        private const float StructuralCrossAxisCoverage = 0.5f;
        private const int MaxColumnTraversalDepth = 12;

        internal sealed class ReferenceGeometry
        {
            internal readonly List<HorizontalReferenceGeometry> SettingsChildren =
                new List<HorizontalReferenceGeometry>();
            internal readonly List<HorizontalReferenceGeometry> CompositionChildren =
                new List<HorizontalReferenceGeometry>();
            internal readonly List<CrossAxisReferenceGeometry> ColumnCrossAxisBranches =
                new List<CrossAxisReferenceGeometry>();
            internal readonly List<VerticalLayoutGroup> ColumnVerticalLayouts =
                new List<VerticalLayoutGroup>();
            internal readonly List<RectTransform> ColumnRoots = new List<RectTransform>();

            internal NestedLayoutReference SettingsLayout;
            internal RectTransform Composition;
            internal RectTransform DropZone;
            internal RectTransform Formations;
            internal RectTransform ActionRow;
            internal RectOffsetGeometry DropZoneMargins;
            internal float ActionRowHeight;
            internal float ActionSpacing;
        }

        internal sealed class HorizontalReferenceGeometry
        {
            internal RectTransform Rect;
            internal float MinFraction;
            internal float MaxFraction;
        }

        internal sealed class CrossAxisReferenceGeometry
        {
            internal RectTransform Rect;
            internal float LeftMargin;
            internal float RightMargin;
        }

        internal sealed class NestedLayoutReference
        {
            internal RectTransform Owner;
            internal LayoutGroup Layout;
            internal readonly List<LayoutChildReference> Children = new List<LayoutChildReference>();
        }

        internal sealed class LayoutChildReference
        {
            internal RectTransform Rect;
            internal float Width;
        }

        internal struct RectOffsetGeometry
        {
            internal float Left;
            internal float Right;
            internal float Top;
            internal float Bottom;
        }

        internal static ReferenceGeometry Capture(
            SquadMaker squadMaker,
            RectTransform squadSettings,
            RectTransform squadComposition)
        {
            if (squadComposition == null)
            {
                return null;
            }

            ReferenceGeometry reference = new ReferenceGeometry
            {
                SettingsLayout = CaptureNestedLayout(squadSettings),
                Composition = squadComposition,
                Formations = FindDirectChildByName(squadComposition, FormationsName),
                ActionRow = FindDirectChildByName(squadComposition, LowerButtonsName)
            };

            RectTransform serializedDropZone = squadMaker != null && squadMaker.DropZone != null
                ? squadMaker.DropZone.transform as RectTransform
                : null;
            reference.DropZone = FindDirectChildAncestor(serializedDropZone, squadComposition);

            if (reference.SettingsLayout == null)
            {
                CaptureNormalizedHorizontalChildren(
                    squadSettings,
                    reference.SettingsChildren,
                    null,
                    null,
                    null);
            }

            CaptureNormalizedHorizontalChildren(
                squadComposition,
                reference.CompositionChildren,
                reference.Formations,
                reference.ActionRow,
                reference.DropZone);

            if (reference.DropZone != null)
            {
                reference.DropZoneMargins = CaptureMargins(squadComposition, reference.DropZone);
            }

            CaptureActionRowMetrics(reference);
            CaptureColumnCrossAxisRelationships(squadMaker, reference);
            ConfigureColorPickerPlacement(squadMaker);
            return reference;
        }

        internal static void Apply(ReferenceGeometry reference)
        {
            if (reference == null || reference.Composition == null)
            {
                return;
            }

            ApplyNestedLayout(reference.SettingsLayout);
            ApplyColumnCrossAxisRelationships(reference);
            ApplyNormalizedHorizontalGeometry(reference.SettingsChildren);
            ApplyNormalizedHorizontalGeometry(reference.CompositionChildren);
            StretchDropZone(reference);
            PinFormationsInsideLeftEdge(reference.Composition, reference.Formations);
            ConfigureActionRow(reference);
        }

        private static NestedLayoutReference CaptureNestedLayout(RectTransform owner)
        {
            if (owner == null)
            {
                return null;
            }

            LayoutGroup layout = owner.GetComponent<LayoutGroup>();
            if (layout == null || !layout.enabled)
            {
                return null;
            }

            NestedLayoutReference reference = new NestedLayoutReference
            {
                Owner = owner,
                Layout = layout
            };

            for (int index = 0; index < owner.childCount; index++)
            {
                RectTransform child = owner.GetChild(index) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                reference.Children.Add(new LayoutChildReference
                {
                    Rect = child,
                    Width = Mathf.Abs(child.rect.width * child.localScale.x)
                });
            }

            return reference;
        }

        private static void ApplyNestedLayout(NestedLayoutReference reference)
        {
            if (reference == null || reference.Owner == null || reference.Layout == null ||
                !reference.Layout.enabled)
            {
                return;
            }

            HorizontalLayoutGroup horizontal = reference.Layout as HorizontalLayoutGroup;
            if (horizontal != null)
            {
                horizontal.childControlWidth = true;
                horizontal.childForceExpandWidth = false;

                for (int index = 0; index < reference.Children.Count; index++)
                {
                    LayoutChildReference childReference = reference.Children[index];
                    if (childReference == null || childReference.Rect == null || childReference.Width <= 0f)
                    {
                        continue;
                    }

                    LayoutElement element = childReference.Rect.GetComponent<LayoutElement>();
                    if (element == null)
                    {
                        element = childReference.Rect.gameObject.AddComponent<LayoutElement>();
                    }

                    element.ignoreLayout = false;
                    element.minWidth = childReference.Width;
                    element.preferredWidth = childReference.Width;
                    element.flexibleWidth = childReference.Width;
                    element.layoutPriority = 1;
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(reference.Owner);
                return;
            }

            VerticalLayoutGroup vertical = reference.Layout as VerticalLayoutGroup;
            if (vertical != null)
            {
                vertical.childControlWidth = true;
                vertical.childForceExpandWidth = true;
                LayoutRebuilder.ForceRebuildLayoutImmediate(reference.Owner);
            }
        }

        private static void CaptureNormalizedHorizontalChildren(
            RectTransform owner,
            List<HorizontalReferenceGeometry> destination,
            RectTransform excludedA,
            RectTransform excludedB,
            RectTransform excludedC)
        {
            if (owner == null || destination == null || HasEnabledLayoutGroup(owner))
            {
                return;
            }

            float ownerWidth = Mathf.Abs(owner.rect.width);
            if (ownerWidth <= GeometryTolerance)
            {
                return;
            }

            for (int index = 0; index < owner.childCount; index++)
            {
                RectTransform child = owner.GetChild(index) as RectTransform;
                if (child == null || child == excludedA || child == excludedB || child == excludedC)
                {
                    continue;
                }

                Rect childBounds = CalculateRectBounds(owner, child);
                destination.Add(new HorizontalReferenceGeometry
                {
                    Rect = child,
                    MinFraction = Mathf.Clamp01((childBounds.xMin - owner.rect.xMin) / ownerWidth),
                    MaxFraction = Mathf.Clamp01((childBounds.xMax - owner.rect.xMin) / ownerWidth)
                });
            }
        }

        private static bool HasEnabledLayoutGroup(RectTransform owner)
        {
            LayoutGroup layout = owner != null ? owner.GetComponent<LayoutGroup>() : null;
            return layout != null && layout.enabled;
        }

        private static void ApplyNormalizedHorizontalGeometry(
            List<HorizontalReferenceGeometry> references)
        {
            if (references == null)
            {
                return;
            }

            for (int index = 0; index < references.Count; index++)
            {
                HorizontalReferenceGeometry reference = references[index];
                RectTransform rect = reference != null ? reference.Rect : null;
                if (rect == null)
                {
                    continue;
                }

                Vector2 anchorMin = rect.anchorMin;
                Vector2 anchorMax = rect.anchorMax;
                Vector2 anchoredPosition = rect.anchoredPosition;
                Vector2 sizeDelta = rect.sizeDelta;

                anchorMin.x = reference.MinFraction;
                anchorMax.x = reference.MaxFraction;
                anchoredPosition.x = 0f;
                sizeDelta.x = 0f;

                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = sizeDelta;
            }
        }

        /// <summary>
        /// The three outer list columns grow proportionally on wide canvases. Unity's direct column
        /// layout can stretch its immediate children, but legacy scroll/list wrappers one level down
        /// can still retain their authored width. Capture only branches that already occupied at least
        /// half of their immediate owner at reference size, and preserve their authored side margins.
        /// This makes structural rows/backers and centered headings inherit live column width without
        /// stretching icons or other deliberately small controls.
        /// </summary>
        private static void CaptureColumnCrossAxisRelationships(
            SquadMaker squadMaker,
            ReferenceGeometry reference)
        {
            if (squadMaker == null || reference == null || squadMaker.ChosenSquadList == null)
            {
                return;
            }

            RectTransform chosenList = squadMaker.ChosenSquadList.transform as RectTransform;
            RectTransform chosenColumn = FindAncestorByName(chosenList, ChosenSquadsColumnName);
            RectTransform squadsColumn = FindAncestorByName(chosenColumn, SquadsColumnName);
            RectTransform mainContainer = FindAncestorByName(squadsColumn, MainContainerName);
            RectTransform savedColumn = FindDirectChildByName(squadsColumn, SavedSquadsColumnName);
            RectTransform shipSelectorColumn = FindDirectChildByName(mainContainer, ShipSelectorColumnName);

            CaptureColumnRoot(shipSelectorColumn, reference);
            CaptureColumnRoot(savedColumn, reference);
            CaptureColumnRoot(chosenColumn, reference);
        }

        private static void CaptureColumnRoot(RectTransform root, ReferenceGeometry reference)
        {
            if (root == null || reference == null)
            {
                return;
            }

            if (!reference.ColumnRoots.Contains(root))
            {
                reference.ColumnRoots.Add(root);
            }

            CaptureColumnNode(root, reference, 0);
        }

        private static void CaptureColumnNode(
            RectTransform current,
            ReferenceGeometry reference,
            int depth)
        {
            if (current == null || reference == null || depth >= MaxColumnTraversalDepth)
            {
                return;
            }

            LayoutGroup ownerLayout = current.GetComponent<LayoutGroup>();
            bool layoutOwnsChildren = ownerLayout != null && ownerLayout.enabled;
            VerticalLayoutGroup vertical = ownerLayout as VerticalLayoutGroup;
            if (vertical != null && !reference.ColumnVerticalLayouts.Contains(vertical))
            {
                reference.ColumnVerticalLayouts.Add(vertical);
            }

            float ownerWidth = Mathf.Abs(current.rect.width);
            for (int index = 0; index < current.childCount; index++)
            {
                RectTransform child = current.GetChild(index) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                if (!layoutOwnsChildren && ownerWidth > GeometryTolerance)
                {
                    Rect bounds = CalculateRectBounds(current, child);
                    float coverage = Mathf.Abs(bounds.width) / ownerWidth;
                    if (coverage >= StructuralCrossAxisCoverage)
                    {
                        reference.ColumnCrossAxisBranches.Add(new CrossAxisReferenceGeometry
                        {
                            Rect = child,
                            LeftMargin = Mathf.Max(0f, bounds.xMin - current.rect.xMin),
                            RightMargin = Mathf.Max(0f, current.rect.xMax - bounds.xMax)
                        });
                    }
                }

                CaptureColumnNode(child, reference, depth + 1);
            }
        }

        private static void ApplyColumnCrossAxisRelationships(ReferenceGeometry reference)
        {
            if (reference == null)
            {
                return;
            }

            for (int index = 0; index < reference.ColumnVerticalLayouts.Count; index++)
            {
                VerticalLayoutGroup layout = reference.ColumnVerticalLayouts[index];
                if (layout == null || !layout.enabled)
                {
                    continue;
                }

                layout.childControlWidth = true;
                layout.childForceExpandWidth = true;
            }

            // References are captured parent-first, so wrappers expand before their descendants.
            for (int index = 0; index < reference.ColumnCrossAxisBranches.Count; index++)
            {
                CrossAxisReferenceGeometry branch = reference.ColumnCrossAxisBranches[index];
                RectTransform rect = branch != null ? branch.Rect : null;
                if (rect == null)
                {
                    continue;
                }

                Vector2 anchorMin = rect.anchorMin;
                Vector2 anchorMax = rect.anchorMax;
                Vector2 offsetMin = rect.offsetMin;
                Vector2 offsetMax = rect.offsetMax;

                anchorMin.x = 0f;
                anchorMax.x = 1f;
                offsetMin.x = branch.LeftMargin;
                offsetMax.x = -branch.RightMargin;

                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.offsetMin = offsetMin;
                rect.offsetMax = offsetMax;
            }

            for (int index = 0; index < reference.ColumnRoots.Count; index++)
            {
                RectTransform root = reference.ColumnRoots[index];
                if (root != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(root);
                }
            }
        }

        private static RectOffsetGeometry CaptureMargins(RectTransform owner, RectTransform rect)
        {
            Rect bounds = CalculateRectBounds(owner, rect);
            return new RectOffsetGeometry
            {
                Left = Mathf.Max(0f, bounds.xMin - owner.rect.xMin),
                Right = Mathf.Max(0f, owner.rect.xMax - bounds.xMax),
                Bottom = Mathf.Max(0f, bounds.yMin - owner.rect.yMin),
                Top = Mathf.Max(0f, owner.rect.yMax - bounds.yMax)
            };
        }

        private static void StretchDropZone(ReferenceGeometry reference)
        {
            RectTransform dropZone = reference.DropZone;
            if (dropZone == null)
            {
                return;
            }

            dropZone.anchorMin = Vector2.zero;
            dropZone.anchorMax = Vector2.one;
            dropZone.offsetMin = new Vector2(
                reference.DropZoneMargins.Left,
                reference.DropZoneMargins.Bottom);
            dropZone.offsetMax = new Vector2(
                -reference.DropZoneMargins.Right,
                -reference.DropZoneMargins.Top);
        }

        private static void PinFormationsInsideLeftEdge(
            RectTransform composition,
            RectTransform formations)
        {
            if (composition == null || formations == null)
            {
                return;
            }

            Vector2 anchorMin = formations.anchorMin;
            Vector2 anchorMax = formations.anchorMax;
            Vector2 anchoredPosition = formations.anchoredPosition;

            // Reset to the semantic left edge first so repeated repairs never accumulate a correction.
            anchorMin.x = 0f;
            anchorMax.x = 0f;
            anchoredPosition.x = 0f;
            formations.anchorMin = anchorMin;
            formations.anchorMax = anchorMax;
            formations.anchoredPosition = anchoredPosition;

            // The Formations parent is narrower than some of its authored child visuals. Aligning only
            // the parent's pivot can therefore leave BLARP protruding on narrow screens. Correct using
            // the complete rendered hierarchy bounds instead.
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);
            float correction = composition.rect.xMin - bounds.min.x;
            anchoredPosition = formations.anchoredPosition;
            anchoredPosition.x += correction;
            formations.anchoredPosition = anchoredPosition;
        }

        private static void CaptureActionRowMetrics(ReferenceGeometry reference)
        {
            RectTransform row = reference.ActionRow;
            if (row == null)
            {
                return;
            }

            List<Rect> childBounds = new List<Rect>();
            float maxHeight = 0f;
            for (int index = 0; index < row.childCount; index++)
            {
                RectTransform child = row.GetChild(index) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                Rect bounds = CalculateRectBounds(row, child);
                childBounds.Add(bounds);
                maxHeight = Mathf.Max(maxHeight, bounds.height);
            }

            childBounds.Sort((left, right) => left.center.x.CompareTo(right.center.x));
            float gapTotal = 0f;
            int gapCount = 0;
            for (int index = 1; index < childBounds.Count; index++)
            {
                float gap = childBounds[index].xMin - childBounds[index - 1].xMax;
                if (gap >= 0f)
                {
                    gapTotal += gap;
                    gapCount++;
                }
            }

            reference.ActionRowHeight = maxHeight > GeometryTolerance
                ? maxHeight
                : Mathf.Abs(row.rect.height);
            reference.ActionSpacing = gapCount > 0 ? gapTotal / gapCount : 0f;
        }

        private static void ConfigureActionRow(ReferenceGeometry reference)
        {
            RectTransform row = reference.ActionRow;
            if (row == null)
            {
                return;
            }

            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0.5f, 0f);
            row.anchoredPosition = Vector2.zero;
            row.sizeDelta = new Vector2(0f, reference.ActionRowHeight);

            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
            {
                layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            }

            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = reference.ActionSpacing;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            LayoutRebuilder.ForceRebuildLayoutImmediate(row);
        }

        private static void ConfigureColorPickerPlacement(SquadMaker squadMaker)
        {
            if (squadMaker == null || squadMaker.ColorPicker == null ||
                squadMaker.SquadColorPickerButton == null)
            {
                return;
            }

            RectTransform picker = squadMaker.ColorPicker.transform as RectTransform;
            RectTransform anchor = squadMaker.SquadColorPickerButton.transform as RectTransform;
            Canvas canvas = anchor != null ? anchor.GetComponentInParent<Canvas>() : null;
            RectTransform viewport = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            if (picker == null || anchor == null || viewport == null)
            {
                return;
            }

            SquadMakerColorPickerPlacementRelay relay =
                picker.GetComponent<SquadMakerColorPickerPlacementRelay>();
            if (relay == null)
            {
                relay = picker.gameObject.AddComponent<SquadMakerColorPickerPlacementRelay>();
            }

            relay.Configure(viewport, anchor);
        }

        /// <summary>
        /// Places an overlay directly below its live anchor, flipping above when needed and clamping
        /// the overlay's complete rendered hierarchy to the viewport. All measurements share viewport
        /// coordinates, so this is independent of screen resolution, CanvasScaler scale, and parent
        /// hierarchy.
        /// </summary>
        internal static bool PositionOverlayNearAnchor(
            RectTransform viewport,
            RectTransform anchor,
            RectTransform overlay)
        {
            if (viewport == null || anchor == null || overlay == null)
            {
                return false;
            }

            Bounds anchorBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, anchor);
            Bounds overlayBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, overlay);
            if (overlayBounds.size.x <= GeometryTolerance || overlayBounds.size.y <= GeometryTolerance)
            {
                return false;
            }

            Rect available = viewport.rect;
            Vector2 correction = new Vector2(
                anchorBounds.center.x - overlayBounds.center.x,
                anchorBounds.min.y - overlayBounds.max.y);

            float belowMinY = overlayBounds.min.y + correction.y;
            if (belowMinY < available.yMin)
            {
                float aboveCorrection = anchorBounds.max.y - overlayBounds.min.y;
                float aboveMaxY = overlayBounds.max.y + aboveCorrection;
                if (aboveMaxY <= available.yMax)
                {
                    correction.y = aboveCorrection;
                }
            }

            MoveRectByViewportDelta(viewport, overlay, correction);

            overlayBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, overlay);
            Vector2 clamp = Vector2.zero;

            if (overlayBounds.size.x <= available.width)
            {
                if (overlayBounds.min.x < available.xMin)
                {
                    clamp.x = available.xMin - overlayBounds.min.x;
                }
                else if (overlayBounds.max.x > available.xMax)
                {
                    clamp.x = available.xMax - overlayBounds.max.x;
                }
            }
            else
            {
                clamp.x = available.center.x - overlayBounds.center.x;
            }

            if (overlayBounds.size.y <= available.height)
            {
                if (overlayBounds.min.y < available.yMin)
                {
                    clamp.y = available.yMin - overlayBounds.min.y;
                }
                else if (overlayBounds.max.y > available.yMax)
                {
                    clamp.y = available.yMax - overlayBounds.max.y;
                }
            }
            else
            {
                clamp.y = available.center.y - overlayBounds.center.y;
            }

            MoveRectByViewportDelta(viewport, overlay, clamp);
            return correction.sqrMagnitude > GeometryTolerance || clamp.sqrMagnitude > GeometryTolerance;
        }

        private static void MoveRectByViewportDelta(
            RectTransform viewport,
            RectTransform rect,
            Vector2 viewportDelta)
        {
            if (viewportDelta.sqrMagnitude <= GeometryTolerance)
            {
                return;
            }

            Vector3 worldDelta = viewport.TransformVector(
                new Vector3(viewportDelta.x, viewportDelta.y, 0f));
            rect.position += worldDelta;
        }

        private static Rect CalculateRectBounds(RectTransform owner, RectTransform rect)
        {
            if (owner == null || rect == null)
            {
                return default;
            }

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector3 first = owner.InverseTransformPoint(corners[0]);
            float minX = first.x;
            float maxX = first.x;
            float minY = first.y;
            float maxY = first.y;

            for (int index = 1; index < corners.Length; index++)
            {
                Vector3 local = owner.InverseTransformPoint(corners[index]);
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static RectTransform FindAncestorByName(RectTransform start, string name)
        {
            RectTransform current = start;
            while (current != null)
            {
                if (current.name == name)
                {
                    return current;
                }

                current = current.parent as RectTransform;
            }

            return null;
        }

        private static RectTransform FindDirectChildByName(RectTransform owner, string name)
        {
            if (owner == null)
            {
                return null;
            }

            for (int index = 0; index < owner.childCount; index++)
            {
                RectTransform child = owner.GetChild(index) as RectTransform;
                if (child != null && child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        private static RectTransform FindDirectChildAncestor(RectTransform descendant, RectTransform owner)
        {
            if (descendant == null || owner == null)
            {
                return null;
            }

            RectTransform current = descendant;
            while (current != null && current.parent != owner)
            {
                current = current.parent as RectTransform;
            }

            return current != null && current.parent == owner ? current : null;
        }
    }

    internal sealed class SquadMakerColorPickerPlacementRelay : MonoBehaviour
    {
        private RectTransform _viewport;
        private RectTransform _anchor;
        private RectTransform _overlay;

        internal void Configure(RectTransform viewport, RectTransform anchor)
        {
            _viewport = viewport;
            _anchor = anchor;
            _overlay = transform as RectTransform;

            if (isActiveAndEnabled)
            {
                Reposition();
            }
        }

        private void OnEnable()
        {
            Reposition();
        }

        private void LateUpdate()
        {
            Reposition();
        }

        private void Reposition()
        {
            SquadMakerCompositionLayoutGuard.PositionOverlayNearAnchor(
                _viewport,
                _anchor,
                _overlay);
        }
    }
}
