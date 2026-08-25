using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Captures and applies the responsive relationships inside Squad Settings and Squad Composition.
    ///
    /// The scene's authored geometry is reference data, not a set of runtime pixel coordinates. Native
    /// LayoutGroups retain ownership of their children. Manual horizontal regions retain their authored
    /// fractions, the real Drop Zone stretches with the work surface, Formations belongs to the
    /// inside-left edge, and the action buttons form one compact row at the bottom. The outer
    /// SquadMakerResponsiveLayoutGuard is the only lifecycle owner and invokes this helper from its
    /// immutable reference snapshot.
    /// </summary>
    internal static class SquadMakerCompositionLayoutGuard
    {
        private const string FormationsName = "Formations";
        private const string LowerButtonsName = "Lower Buttons";
        private const float GeometryTolerance = 0.001f;

        internal sealed class ReferenceGeometry
        {
            internal readonly List<HorizontalReferenceGeometry> SettingsChildren =
                new List<HorizontalReferenceGeometry>();
            internal readonly List<HorizontalReferenceGeometry> CompositionChildren =
                new List<HorizontalReferenceGeometry>();

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
            return reference;
        }

        internal static void Apply(ReferenceGeometry reference)
        {
            if (reference == null || reference.Composition == null)
            {
                return;
            }

            ApplyNestedLayout(reference.SettingsLayout);
            ApplyNormalizedHorizontalGeometry(reference.SettingsChildren);
            ApplyNormalizedHorizontalGeometry(reference.CompositionChildren);
            StretchDropZone(reference);
            PinFormationsToLeftEdge(reference.Formations);
            ConfigureActionRow(reference);
        }

        private static NestedLayoutReference CaptureNestedLayout(RectTransform owner)
        {
            if (owner == null)
            {
                return null;
            }

            LayoutGroup layout = owner.GetComponent<LayoutGroup>();
            if (layout == null)
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
            if (reference == null || reference.Owner == null || reference.Layout == null)
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
            if (owner == null || destination == null || owner.GetComponent<LayoutGroup>() != null)
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

        private static void PinFormationsToLeftEdge(RectTransform formations)
        {
            if (formations == null)
            {
                return;
            }

            Vector2 anchorMin = formations.anchorMin;
            Vector2 anchorMax = formations.anchorMax;
            Vector2 anchoredPosition = formations.anchoredPosition;

            anchorMin.x = 0f;
            anchorMax.x = 0f;
            anchoredPosition.x = formations.pivot.x * Mathf.Abs(formations.rect.width * formations.localScale.x);

            formations.anchorMin = anchorMin;
            formations.anchorMax = anchorMax;
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
}
