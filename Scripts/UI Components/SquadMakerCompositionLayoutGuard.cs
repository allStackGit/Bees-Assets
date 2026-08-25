using Assets.Scripts.Scenes;
using Assets.Scripts.UIComponents;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Captures and applies responsive relationships inside the Squad Maker's authored subregions.
    /// Reference geometry is immutable. Structural LayoutGroups keep structural ownership, while
    /// manual regions are converted to stable semantic relationships (edge ownership, proportions,
    /// flexible fields, and bounded overlays) rather than live-resolution coordinates.
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
        private const string SquadPresetsHeading = "Squad Presets";
        private const string SquadNameObjectName = "Squad Name";
        private const string SquadNumberObjectName = "Squad Number";
        private const string RuntimeIconContainerName = "Icon Container";
        private const string LegacySquadIconName = "Squad Icon";
        private const float GeometryTolerance = 0.001f;
        private const float StructuralCrossAxisCoverage = 0.5f;
        private const float SettingsStructuralCrossAxisCoverage = 0.6f;
        private const float OverlayGap = 4f;
        private const int MaxColumnTraversalDepth = 12;
        private const int MaxSettingsTraversalDepth = 8;

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
            internal readonly List<CrossAxisReferenceGeometry> SettingsCrossAxisBranches =
                new List<CrossAxisReferenceGeometry>();
            internal readonly List<VerticalLayoutGroup> SettingsVerticalLayouts =
                new List<VerticalLayoutGroup>();
            internal readonly List<RectTransform> SettingsOverlays = new List<RectTransform>();

            internal NestedLayoutReference SettingsLayout;
            internal HeaderReferenceGeometry Header;
            internal RectTransform Settings;
            internal RectTransform Composition;
            internal RectTransform DropZone;
            internal RectTransform Formations;
            internal RectTransform ActionRow;
            internal RectTransform SavedSquadList;
            internal RectTransform ChosenSquadList;
            internal RectOffsetGeometry DropZoneMargins;
            internal Vector3 FormationsReferenceScale;
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

        internal sealed class HeaderReferenceGeometry
        {
            internal RectTransform OuterOwner;
            internal RectTransform Owner;
            internal RectTransform Supply;
            internal RectTransform Name;
            internal RectTransform Color;
            internal RectTransform Count;
            internal float OwnerWidth;
            internal float OwnerLeftMargin;
            internal float OwnerRightMargin;
            internal float LeftMargin;
            internal float SupplyWidth;
            internal float SupplyNameGap;
            internal float NameWidth;
            internal float NameColorGap;
            internal float ColorWidth;
            internal float ColorCountGap;
            internal float CountWidth;
            internal float RightMargin;
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
                Settings = squadSettings,
                SettingsLayout = CaptureNestedLayout(squadSettings),
                Composition = squadComposition,
                Formations = FindDirectChildByName(squadComposition, FormationsName),
                ActionRow = FindDirectChildByName(squadComposition, LowerButtonsName),
                SavedSquadList = squadMaker != null && squadMaker.SavedSquadList != null
                    ? squadMaker.SavedSquadList.transform as RectTransform
                    : null,
                ChosenSquadList = squadMaker != null && squadMaker.ChosenSquadList != null
                    ? squadMaker.ChosenSquadList.transform as RectTransform
                    : null
            };

            if (reference.Formations != null)
            {
                reference.FormationsReferenceScale = reference.Formations.localScale;
            }

            RectTransform serializedDropZone = squadMaker != null && squadMaker.DropZone != null
                ? squadMaker.DropZone.transform as RectTransform
                : null;
            reference.DropZone = FindDirectChildAncestor(serializedDropZone, squadComposition);
            reference.Header = CaptureHeaderReference(squadMaker, squadComposition);

            if (reference.SettingsLayout == null)
            {
                CaptureNormalizedHorizontalChildren(
                    squadSettings,
                    reference.SettingsChildren);
            }

            CaptureSettingsCrossAxisRelationships(squadSettings, reference);
            CaptureSettingsOverlay(
                squadSettings,
                squadMaker != null ? squadMaker.ShipInfoBox : null,
                reference);
            CaptureSettingsOverlay(
                squadSettings,
                squadMaker != null ? squadMaker.SquadInfoBox : null,
                reference);

            CaptureNormalizedHorizontalChildren(
                squadComposition,
                reference.CompositionChildren,
                reference.Formations,
                reference.ActionRow,
                reference.DropZone,
                reference.Header != null && reference.Header.Owner != squadComposition
                    ? reference.Header.Owner
                    : null,
                reference.Header != null ? reference.Header.Supply : null,
                reference.Header != null ? reference.Header.Name : null,
                reference.Header != null ? reference.Header.Color : null,
                reference.Header != null ? reference.Header.Count : null);

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
            ApplySettingsCrossAxisRelationships(reference);
            ApplyColumnCrossAxisRelationships(reference);
            ApplyNormalizedHorizontalGeometry(reference.SettingsChildren);
            ApplySettingsOverlays(reference);
            ApplyNormalizedHorizontalGeometry(reference.CompositionChildren);
            ApplyHeaderLayout(reference.Header);
            StretchDropZone(reference);
            PinFormationsInsideWorkArea(reference);
            ConfigureActionRow(reference);
            CenterHeading(reference.Settings, SquadPresetsHeading);
            ConfigureSquadListRows(reference.SavedSquadList);
            ConfigureSquadListRows(reference.ChosenSquadList);
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

                float totalWidth = 0f;
                for (int index = 0; index < reference.Children.Count; index++)
                {
                    LayoutChildReference childReference = reference.Children[index];
                    if (childReference != null && childReference.Rect != null && childReference.Width > 0f)
                    {
                        totalWidth += childReference.Width;
                    }
                }

                float ownerWidth = Mathf.Max(
                    0f,
                    reference.Owner.rect.width -
                    horizontal.padding.left -
                    horizontal.padding.right -
                    Mathf.Max(0, reference.Children.Count - 1) * horizontal.spacing);

                for (int index = 0; index < reference.Children.Count; index++)
                {
                    LayoutChildReference childReference = reference.Children[index];
                    if (childReference == null || childReference.Rect == null || childReference.Width <= 0f)
                    {
                        continue;
                    }

                    float targetWidth = totalWidth > GeometryTolerance
                        ? ownerWidth * (childReference.Width / totalWidth)
                        : childReference.Width;
                    LayoutElement element = GetOrAddLayoutElement(childReference.Rect);
                    element.ignoreLayout = false;
                    element.minWidth = targetWidth;
                    element.preferredWidth = targetWidth;
                    element.flexibleWidth = 0f;
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

        private static HeaderReferenceGeometry CaptureHeaderReference(
            SquadMaker squadMaker,
            RectTransform composition)
        {
            if (squadMaker == null || composition == null)
            {
                return null;
            }

            RectTransform supplyLeaf = squadMaker.SquadMakerSupplyCapacityLabel != null
                ? squadMaker.SquadMakerSupplyCapacityLabel.transform as RectTransform
                : null;
            RectTransform nameLeaf = squadMaker.SquadNameInput != null
                ? squadMaker.SquadNameInput.transform as RectTransform
                : null;
            RectTransform colorLeaf = squadMaker.SquadColorPickerButton != null
                ? squadMaker.SquadColorPickerButton.transform as RectTransform
                : null;
            RectTransform countLeaf = squadMaker.SquadShipCount != null
                ? squadMaker.SquadShipCount.transform as RectTransform
                : null;

            if (!IsDescendantOrSelf(supplyLeaf, composition) ||
                !IsDescendantOrSelf(nameLeaf, composition) ||
                !IsDescendantOrSelf(colorLeaf, composition) ||
                !IsDescendantOrSelf(countLeaf, composition))
            {
                return null;
            }

            RectTransform owner = FindLowestCommonAncestorWithin(
                composition,
                supplyLeaf,
                nameLeaf,
                colorLeaf,
                countLeaf);
            if (owner == null)
            {
                return null;
            }

            RectTransform supply = ResolveHeaderBranch(supplyLeaf, owner);
            RectTransform name = ResolveHeaderBranch(nameLeaf, owner);
            RectTransform color = ResolveHeaderBranch(colorLeaf, owner);
            RectTransform count = ResolveHeaderBranch(countLeaf, owner);

            if (!AreDistinctHeaderBranches(supply, name, color, count))
            {
                // Some authoring hierarchies wrap more than one semantic control together. In that
                // case use the actual referenced RectTransforms; SetHorizontalBoundsInOwner supports
                // descendants, so responsiveness still follows semantics rather than hierarchy depth.
                supply = supplyLeaf;
                name = nameLeaf;
                color = colorLeaf;
                count = countLeaf;
            }

            Rect supplyBounds = CalculateRectBounds(owner, supply);
            Rect nameBounds = CalculateRectBounds(owner, name);
            Rect colorBounds = CalculateRectBounds(owner, color);
            Rect countBounds = CalculateRectBounds(owner, count);
            if (supplyBounds.xMin > nameBounds.xMin || nameBounds.xMin > colorBounds.xMin ||
                colorBounds.xMin > countBounds.xMin)
            {
                return null;
            }

            Rect ownerBounds = CalculateRectBounds(composition, owner);
            return new HeaderReferenceGeometry
            {
                OuterOwner = composition,
                Owner = owner,
                Supply = supply,
                Name = name,
                Color = color,
                Count = count,
                OwnerWidth = Mathf.Abs(ownerBounds.width),
                OwnerLeftMargin = Mathf.Max(0f, ownerBounds.xMin - composition.rect.xMin),
                OwnerRightMargin = Mathf.Max(0f, composition.rect.xMax - ownerBounds.xMax),
                LeftMargin = Mathf.Max(0f, supplyBounds.xMin - owner.rect.xMin),
                SupplyWidth = supplyBounds.width,
                SupplyNameGap = Mathf.Max(0f, nameBounds.xMin - supplyBounds.xMax),
                NameWidth = nameBounds.width,
                NameColorGap = Mathf.Max(0f, colorBounds.xMin - nameBounds.xMax),
                ColorWidth = colorBounds.width,
                ColorCountGap = Mathf.Max(0f, countBounds.xMin - colorBounds.xMax),
                CountWidth = countBounds.width,
                RightMargin = Mathf.Max(0f, owner.rect.xMax - countBounds.xMax)
            };
        }

        private static void ApplyHeaderLayout(HeaderReferenceGeometry reference)
        {
            if (reference == null || reference.OuterOwner == null || reference.Owner == null ||
                reference.Supply == null || reference.Name == null || reference.Color == null ||
                reference.Count == null || reference.OwnerWidth <= GeometryTolerance)
            {
                return;
            }

            if (reference.Owner != reference.OuterOwner)
            {
                float availableWidth = Mathf.Max(
                    0f,
                    reference.OuterOwner.rect.width -
                    reference.OwnerLeftMargin -
                    reference.OwnerRightMargin);
                SetHorizontalBoundsInOwner(
                    reference.OuterOwner,
                    reference.Owner,
                    reference.OuterOwner.rect.xMin + reference.OwnerLeftMargin,
                    availableWidth);
            }

            float liveWidth = Mathf.Abs(CalculateRectBounds(reference.OuterOwner, reference.Owner).width);
            if (liveWidth <= GeometryTolerance)
            {
                return;
            }

            float scale = Mathf.Min(1f, liveWidth / reference.OwnerWidth);
            float leftMargin = reference.LeftMargin * scale;
            float supplyWidth = reference.SupplyWidth * scale;
            float supplyNameGap = reference.SupplyNameGap * scale;
            float nameWidth = reference.NameWidth * scale;
            float nameColorGap = reference.NameColorGap * scale;
            float colorWidth = reference.ColorWidth * scale;
            float colorCountGap = reference.ColorCountGap * scale;
            float countWidth = reference.CountWidth * scale;
            float rightMargin = reference.RightMargin * scale;

            if (liveWidth > reference.OwnerWidth)
            {
                nameWidth += liveWidth - reference.OwnerWidth;
            }

            Rect liveOwnerBounds = CalculateRectBounds(reference.Owner, reference.Owner);
            float cursor = liveOwnerBounds.xMin + leftMargin;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Supply, cursor, supplyWidth);
            cursor += supplyWidth + supplyNameGap;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Name, cursor, Mathf.Max(0f, nameWidth));
            cursor += nameWidth + nameColorGap;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Color, cursor, colorWidth);
            cursor += colorWidth + colorCountGap;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Count, cursor, countWidth);

            Rect countBounds = CalculateRectBounds(reference.Owner, reference.Count);
            float expectedRight = liveOwnerBounds.xMax - rightMargin;
            if (Mathf.Abs(countBounds.xMax - expectedRight) > GeometryTolerance)
            {
                SetHorizontalBoundsInOwner(
                    reference.Owner,
                    reference.Count,
                    expectedRight - countWidth,
                    countWidth);
            }

            StretchNestedInputVisual(reference.Name);
        }

        private static void StretchNestedInputVisual(RectTransform headerNameOwner)
        {
            if (headerNameOwner == null)
            {
                return;
            }

            TMP_InputField input = headerNameOwner.GetComponent<TMP_InputField>();
            if (input != null)
            {
                return;
            }

            input = headerNameOwner.GetComponentInChildren<TMP_InputField>(true);
            if (input == null)
            {
                return;
            }

            RectTransform inputRect = input.transform as RectTransform;
            RectTransform directChild = FindDirectChildAncestor(inputRect, headerNameOwner);
            if (directChild != null && directChild != headerNameOwner)
            {
                StretchHorizontal(directChild, 0f, 0f);
            }
        }

        private static void SetHorizontalBoundsInOwner(
            RectTransform owner,
            RectTransform rect,
            float left,
            float width)
        {
            if (owner == null || rect == null)
            {
                return;
            }

            Rect current = CalculateRectBounds(owner, rect);
            float targetWidth = Mathf.Max(0f, width);
            if (current.width > GeometryTolerance)
            {
                float localWidth = Mathf.Abs(rect.rect.width);
                if (localWidth > GeometryTolerance)
                {
                    rect.SetSizeWithCurrentAnchors(
                        RectTransform.Axis.Horizontal,
                        localWidth * (targetWidth / current.width));
                }
            }
            else
            {
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
            }

            current = CalculateRectBounds(owner, rect);
            MoveRectByOwnerDelta(owner, rect, new Vector2(left - current.xMin, 0f));
        }

        private static void CaptureNormalizedHorizontalChildren(
            RectTransform owner,
            List<HorizontalReferenceGeometry> destination,
            params RectTransform[] excluded)
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
                if (child == null || IsExcluded(child, excluded))
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

        private static bool IsExcluded(RectTransform rect, RectTransform[] excluded)
        {
            if (rect == null || excluded == null)
            {
                return false;
            }

            for (int index = 0; index < excluded.Length; index++)
            {
                if (excluded[index] == rect)
                {
                    return true;
                }
            }

            return false;
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

        private static void CaptureSettingsCrossAxisRelationships(
            RectTransform settings,
            ReferenceGeometry reference)
        {
            if (settings != null && reference != null)
            {
                CaptureSettingsNode(settings, reference, 0);
            }
        }

        private static void CaptureSettingsNode(
            RectTransform current,
            ReferenceGeometry reference,
            int depth)
        {
            if (current == null || reference == null || depth >= MaxSettingsTraversalDepth)
            {
                return;
            }

            LayoutGroup ownerLayout = current.GetComponent<LayoutGroup>();
            bool layoutEnabled = ownerLayout != null && ownerLayout.enabled;
            VerticalLayoutGroup vertical = ownerLayout as VerticalLayoutGroup;
            if (vertical != null && !reference.SettingsVerticalLayouts.Contains(vertical))
            {
                reference.SettingsVerticalLayouts.Add(vertical);
            }

            float ownerWidth = Mathf.Abs(current.rect.width);
            for (int index = 0; index < current.childCount; index++)
            {
                RectTransform child = current.GetChild(index) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                LayoutElement layoutElement = child.GetComponent<LayoutElement>();
                bool layoutOwnsChild = layoutEnabled &&
                    (layoutElement == null || !layoutElement.ignoreLayout);
                if (!layoutOwnsChild && ownerWidth > GeometryTolerance)
                {
                    Rect bounds = CalculateRectBounds(current, child);
                    float coverage = Mathf.Abs(bounds.width) / ownerWidth;
                    if (coverage >= SettingsStructuralCrossAxisCoverage)
                    {
                        bool isPresentationBacker = child.GetComponent<Image>() != null;
                        if (isPresentationBacker)
                        {
                            reference.SettingsCrossAxisBranches.Add(new CrossAxisReferenceGeometry
                            {
                                Rect = child,
                                LeftMargin = 0f,
                                RightMargin = 0f
                            });
                        }
                        else
                        {
                            AddCrossAxisReference(
                                reference.SettingsCrossAxisBranches,
                                current,
                                child,
                                bounds);
                        }
                    }
                }

                CaptureSettingsNode(child, reference, depth + 1);
            }
        }

        private static void CaptureSettingsOverlay(
            RectTransform settings,
            GameObject overlayObject,
            ReferenceGeometry reference)
        {
            if (settings == null || overlayObject == null || reference == null)
            {
                return;
            }

            RectTransform overlayRect = overlayObject.transform as RectTransform;
            RectTransform branch = FindDirectChildAncestor(overlayRect, settings);
            if (branch != null && branch != settings && !reference.SettingsOverlays.Contains(branch))
            {
                reference.SettingsOverlays.Add(branch);
            }
        }

        private static void ApplySettingsCrossAxisRelationships(ReferenceGeometry reference)
        {
            if (reference == null)
            {
                return;
            }

            for (int index = 0; index < reference.SettingsVerticalLayouts.Count; index++)
            {
                VerticalLayoutGroup layout = reference.SettingsVerticalLayouts[index];
                if (layout != null && layout.enabled)
                {
                    layout.childControlWidth = true;
                    layout.childForceExpandWidth = true;
                }
            }

            ApplyCrossAxisReferences(reference.SettingsCrossAxisBranches);
            if (reference.Settings != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(reference.Settings);
            }
        }

        private static void ApplySettingsOverlays(ReferenceGeometry reference)
        {
            if (reference == null)
            {
                return;
            }

            for (int index = 0; index < reference.SettingsOverlays.Count; index++)
            {
                RectTransform overlay = reference.SettingsOverlays[index];
                if (overlay == null)
                {
                    continue;
                }

                LayoutElement element = GetOrAddLayoutElement(overlay);
                element.ignoreLayout = true;
                StretchHorizontal(overlay, 0f, 0f);
            }
        }

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

        private static void CaptureColumnRoot(
            RectTransform root,
            ReferenceGeometry reference)
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
                    if (Mathf.Abs(bounds.width) / ownerWidth >= StructuralCrossAxisCoverage)
                    {
                        AddCrossAxisReference(
                            reference.ColumnCrossAxisBranches,
                            current,
                            child,
                            bounds);
                    }
                }
                CaptureColumnNode(child, reference, depth + 1);
            }
        }

        private static void AddCrossAxisReference(
            List<CrossAxisReferenceGeometry> destination,
            RectTransform owner,
            RectTransform child,
            Rect bounds)
        {
            if (destination == null || owner == null || child == null)
            {
                return;
            }

            destination.Add(new CrossAxisReferenceGeometry
            {
                Rect = child,
                LeftMargin = Mathf.Max(0f, bounds.xMin - owner.rect.xMin),
                RightMargin = Mathf.Max(0f, owner.rect.xMax - bounds.xMax)
            });
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
                if (layout != null && layout.enabled)
                {
                    layout.childControlWidth = true;
                    layout.childForceExpandWidth = true;
                }
            }

            ApplyCrossAxisReferences(reference.ColumnCrossAxisBranches);
            for (int index = 0; index < reference.ColumnRoots.Count; index++)
            {
                RectTransform root = reference.ColumnRoots[index];
                if (root != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(root);
                }
            }
        }

        private static void ApplyCrossAxisReferences(
            List<CrossAxisReferenceGeometry> references)
        {
            if (references == null)
            {
                return;
            }

            for (int index = 0; index < references.Count; index++)
            {
                CrossAxisReferenceGeometry branch = references[index];
                RectTransform rect = branch != null ? branch.Rect : null;
                if (rect != null)
                {
                    StretchHorizontal(rect, branch.LeftMargin, branch.RightMargin);
                }
            }
        }

        private static void StretchHorizontal(
            RectTransform rect,
            float leftMargin,
            float rightMargin)
        {
            if (rect == null)
            {
                return;
            }

            Vector2 anchorMin = rect.anchorMin;
            Vector2 anchorMax = rect.anchorMax;
            Vector2 offsetMin = rect.offsetMin;
            Vector2 offsetMax = rect.offsetMax;
            anchorMin.x = 0f;
            anchorMax.x = 1f;
            offsetMin.x = leftMargin;
            offsetMax.x = -rightMargin;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static RectOffsetGeometry CaptureMargins(
            RectTransform owner,
            RectTransform rect)
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
            RectTransform dropZone = reference != null ? reference.DropZone : null;
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

        private static void PinFormationsInsideWorkArea(ReferenceGeometry reference)
        {
            RectTransform composition = reference != null ? reference.Composition : null;
            RectTransform formations = reference != null ? reference.Formations : null;
            RectTransform dropZone = reference != null ? reference.DropZone : null;
            if (composition == null || formations == null)
            {
                return;
            }

            formations.localScale = reference.FormationsReferenceScale;
            LayoutRebuilder.ForceRebuildLayoutImmediate(formations);
            Canvas.ForceUpdateCanvases();

            Vector2 anchorMin = formations.anchorMin;
            Vector2 anchorMax = formations.anchorMax;
            Vector2 anchoredPosition = formations.anchoredPosition;
            anchorMin.x = 0f;
            anchorMax.x = 0f;
            anchoredPosition.x = 0f;
            formations.anchorMin = anchorMin;
            formations.anchorMax = anchorMax;
            formations.anchoredPosition = anchoredPosition;

            Rect workBounds = dropZone != null
                ? CalculateRectBounds(composition, dropZone)
                : composition.rect;
            Bounds formationBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);

            if (formationBounds.size.x > workBounds.width + GeometryTolerance &&
                formationBounds.size.x > 0f)
            {
                Vector3 scale = formations.localScale;
                scale.x *= workBounds.width / formationBounds.size.x;
                formations.localScale = scale;
                Canvas.ForceUpdateCanvases();
                formationBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    composition,
                    formations);
            }

            float correction = workBounds.xMin - formationBounds.min.x;
            MoveRectByOwnerDelta(composition, formations, new Vector2(correction, 0f));

            formationBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);
            if (formationBounds.max.x > workBounds.xMax)
            {
                MoveRectByOwnerDelta(
                    composition,
                    formations,
                    new Vector2(workBounds.xMax - formationBounds.max.x, 0f));
            }
        }

        private static void CaptureActionRowMetrics(ReferenceGeometry reference)
        {
            RectTransform row = reference != null ? reference.ActionRow : null;
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
            RectTransform row = reference != null ? reference.ActionRow : null;
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

        private static void CenterHeading(
            RectTransform owner,
            string heading)
        {
            if (owner == null || string.IsNullOrEmpty(heading))
            {
                return;
            }

            TMP_Text[] labels = owner.GetComponentsInChildren<TMP_Text>(true);
            for (int index = 0; index < labels.Length; index++)
            {
                TMP_Text label = labels[index];
                if (label == null || !string.Equals(
                    label.text?.Trim(),
                    heading,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                RectTransform rect = label.rectTransform;
                RectTransform parent = rect != null ? rect.parent as RectTransform : null;
                LayoutGroup parentLayout = parent != null ? parent.GetComponent<LayoutGroup>() : null;
                LayoutElement element = rect != null ? rect.GetComponent<LayoutElement>() : null;
                bool layoutOwnsLabel = parentLayout != null && parentLayout.enabled &&
                    (element == null || !element.ignoreLayout);

                if (!layoutOwnsLabel && rect != null)
                {
                    StretchHorizontal(rect, 0f, 0f);
                }
                label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            }
        }

        private static void ConfigureSquadListRows(RectTransform listRoot)
        {
            if (listRoot == null)
            {
                return;
            }

            for (int index = 0; index < listRoot.childCount; index++)
            {
                RectTransform row = listRoot.GetChild(index) as RectTransform;
                if (row == null)
                {
                    continue;
                }

                TMP_Text label = FindSquadRowLabel(row);
                if (label == null)
                {
                    continue;
                }

                HorizontalLayoutGroup competingLayout = row.GetComponent<HorizontalLayoutGroup>();
                if (competingLayout != null && competingLayout.enabled)
                {
                    // Preserve its current vertical placement once, then remove it as a competing
                    // horizontal geometry writer. All responsive x-geometry below is deterministic.
                    LayoutRebuilder.ForceRebuildLayoutImmediate(row);
                    competingLayout.enabled = false;
                }

                RectTransform runtimeIcon = FindDirectChildByName(row, RuntimeIconContainerName);
                RectTransform legacyIcon = FindDirectChildByName(row, LegacySquadIconName);
                if (runtimeIcon != null && legacyIcon != null && legacyIcon.gameObject.activeSelf)
                {
                    legacyIcon.gameObject.SetActive(false);
                }
                RectTransform icon = runtimeIcon != null ? runtimeIcon : legacyIcon;

                float gap = Mathf.Max(4f, Mathf.Abs(row.rect.height) * 0.15f);
                float iconRight = row.rect.xMin;
                if (icon != null && icon.gameObject.activeInHierarchy)
                {
                    Bounds iconBounds =
                        RectTransformUtility.CalculateRelativeRectTransformBounds(row, icon);
                    float targetMin = row.rect.xMin + gap;
                    MoveRectByOwnerDelta(
                        row,
                        icon,
                        new Vector2(targetMin - iconBounds.min.x, 0f));
                    iconBounds =
                        RectTransformUtility.CalculateRelativeRectTransformBounds(row, icon);
                    iconRight = iconBounds.max.x;
                }

                RectTransform labelRect = label.rectTransform;
                Vector2 anchorMin = labelRect.anchorMin;
                Vector2 anchorMax = labelRect.anchorMax;
                Vector2 offsetMin = labelRect.offsetMin;
                Vector2 offsetMax = labelRect.offsetMax;
                anchorMin.x = 0f;
                anchorMax.x = 1f;
                offsetMin.x = Mathf.Max(gap, iconRight - row.rect.xMin + gap);
                offsetMax.x = -gap;
                labelRect.anchorMin = anchorMin;
                labelRect.anchorMax = anchorMax;
                labelRect.offsetMin = offsetMin;
                labelRect.offsetMax = offsetMax;
                label.horizontalAlignment = HorizontalAlignmentOptions.Left;
            }
        }

        private static TMP_Text FindSquadRowLabel(RectTransform row)
        {
            if (row == null)
            {
                return null;
            }

            Transform exactName = row.Find(SquadNameObjectName);
            if (exactName != null)
            {
                TMP_Text exact = exactName.GetComponent<TMP_Text>();
                if (exact != null)
                {
                    return exact;
                }
            }

            Transform exactNumber = row.Find(SquadNumberObjectName);
            if (exactNumber != null)
            {
                TMP_Text exact = exactNumber.GetComponent<TMP_Text>();
                if (exact != null)
                {
                    return exact;
                }
            }

            TMP_Text[] labels = row.GetComponentsInChildren<TMP_Text>(true);
            for (int index = 0; index < labels.Length; index++)
            {
                TMP_Text label = labels[index];
                string text = label != null ? label.text : null;
                if (!string.IsNullOrWhiteSpace(text) &&
                    text.TrimStart().StartsWith("Squad", StringComparison.OrdinalIgnoreCase))
                {
                    return label;
                }
            }
            return null;
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
            RectTransform viewport = canvas != null
                ? canvas.rootCanvas.transform as RectTransform
                : null;
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

        internal static bool PositionOverlayNearAnchor(
            RectTransform viewport,
            RectTransform anchor,
            RectTransform overlay)
        {
            if (viewport == null || anchor == null || overlay == null)
            {
                return false;
            }

            Rect anchorBounds = CalculateRectBounds(viewport, anchor);
            Rect overlayRootBounds = CalculateRectBounds(viewport, overlay);
            if (overlayRootBounds.width <= GeometryTolerance ||
                overlayRootBounds.height <= GeometryTolerance)
            {
                return false;
            }

            Rect available = viewport.rect;
            Vector2 correction = new Vector2(
                anchorBounds.center.x - overlayRootBounds.center.x,
                (anchorBounds.yMin - OverlayGap) - overlayRootBounds.yMax);

            float belowMinY = overlayRootBounds.yMin + correction.y;
            if (belowMinY < available.yMin)
            {
                float aboveCorrection =
                    (anchorBounds.yMax + OverlayGap) - overlayRootBounds.yMin;
                float aboveMaxY = overlayRootBounds.yMax + aboveCorrection;
                if (aboveMaxY <= available.yMax)
                {
                    correction.y = aboveCorrection;
                }
            }

            MoveRectByOwnerDelta(viewport, overlay, correction);

            Bounds renderedBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, overlay);
            Vector2 clamp = Vector2.zero;
            if (renderedBounds.size.x <= available.width)
            {
                if (renderedBounds.min.x < available.xMin)
                {
                    clamp.x = available.xMin - renderedBounds.min.x;
                }
                else if (renderedBounds.max.x > available.xMax)
                {
                    clamp.x = available.xMax - renderedBounds.max.x;
                }
            }
            else
            {
                clamp.x = available.center.x - renderedBounds.center.x;
            }

            if (renderedBounds.size.y <= available.height)
            {
                if (renderedBounds.min.y < available.yMin)
                {
                    clamp.y = available.yMin - renderedBounds.min.y;
                }
                else if (renderedBounds.max.y > available.yMax)
                {
                    clamp.y = available.yMax - renderedBounds.max.y;
                }
            }
            else
            {
                clamp.y = available.center.y - renderedBounds.center.y;
            }

            MoveRectByOwnerDelta(viewport, overlay, clamp);
            return correction.sqrMagnitude > GeometryTolerance ||
                clamp.sqrMagnitude > GeometryTolerance;
        }

        private static void MoveRectByOwnerDelta(
            RectTransform owner,
            RectTransform rect,
            Vector2 ownerDelta)
        {
            if (owner == null || rect == null || ownerDelta.sqrMagnitude <= GeometryTolerance)
            {
                return;
            }

            Vector3 worldDelta = owner.TransformVector(
                new Vector3(ownerDelta.x, ownerDelta.y, 0f));
            rect.position += worldDelta;
        }

        private static Rect CalculateRectBounds(
            RectTransform owner,
            RectTransform rect)
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

        private static LayoutElement GetOrAddLayoutElement(RectTransform rect)
        {
            LayoutElement element = rect != null ? rect.GetComponent<LayoutElement>() : null;
            if (element == null && rect != null)
            {
                element = rect.gameObject.AddComponent<LayoutElement>();
            }
            return element;
        }

        private static bool IsDescendantOrSelf(
            RectTransform descendant,
            RectTransform owner)
        {
            Transform current = descendant;
            while (current != null)
            {
                if (current == owner)
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }

        private static RectTransform FindLowestCommonAncestorWithin(
            RectTransform boundary,
            params RectTransform[] rects)
        {
            if (boundary == null || rects == null || rects.Length == 0 || rects[0] == null)
            {
                return null;
            }

            RectTransform candidate = rects[0];
            while (candidate != null)
            {
                bool containsAll = true;
                for (int index = 1; index < rects.Length; index++)
                {
                    if (!IsDescendantOrSelf(rects[index], candidate))
                    {
                        containsAll = false;
                        break;
                    }
                }

                if (containsAll)
                {
                    return candidate;
                }
                if (candidate == boundary)
                {
                    break;
                }
                candidate = candidate.parent as RectTransform;
            }
            return null;
        }

        private static RectTransform ResolveHeaderBranch(
            RectTransform leaf,
            RectTransform owner)
        {
            if (leaf == null || owner == null)
            {
                return null;
            }
            if (leaf == owner)
            {
                return leaf;
            }
            return FindDirectChildAncestor(leaf, owner);
        }

        private static bool AreDistinctHeaderBranches(
            RectTransform supply,
            RectTransform name,
            RectTransform color,
            RectTransform count)
        {
            return supply != null && name != null && color != null && count != null &&
                supply != name && supply != color && supply != count &&
                name != color && name != count && color != count;
        }

        private static RectTransform FindAncestorByName(
            RectTransform start,
            string name)
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

        private static RectTransform FindDirectChildByName(
            RectTransform owner,
            string name)
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

        private static RectTransform FindDirectChildAncestor(
            RectTransform descendant,
            RectTransform owner)
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
        private ColorPicker _picker;

        internal void Configure(
            RectTransform viewport,
            RectTransform anchor)
        {
            _viewport = viewport;
            _anchor = anchor;
            _overlay = transform as RectTransform;
            _picker = GetComponent<ColorPicker>();
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
            if (_picker != null)
            {
                _picker.PrepareResponsiveGeometry();
            }
            SquadMakerCompositionLayoutGuard.PositionOverlayNearAnchor(
                _viewport,
                _anchor,
                _overlay);
        }
    }
}
