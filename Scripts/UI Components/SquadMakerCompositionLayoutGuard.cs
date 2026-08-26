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
    /// Captures and applies responsive relationships inside Squad Maker subregions.
    /// Reference geometry is immutable; live geometry is always derived from that snapshot.
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
        private const float MaximumNameWidthScale = 1.5f;
        private const int MaxColumnTraversalDepth = 12;
        private const int MaxSettingsTraversalDepth = 8;

        internal sealed class ReferenceGeometry
        {
            internal readonly List<HorizontalReferenceGeometry> SettingsChildren = new List<HorizontalReferenceGeometry>();
            internal readonly List<HorizontalReferenceGeometry> CompositionChildren = new List<HorizontalReferenceGeometry>();
            internal readonly List<CrossAxisReferenceGeometry> ColumnCrossAxisBranches = new List<CrossAxisReferenceGeometry>();
            internal readonly List<VerticalLayoutGroup> ColumnVerticalLayouts = new List<VerticalLayoutGroup>();
            internal readonly List<RectTransform> ColumnRoots = new List<RectTransform>();
            internal readonly List<CrossAxisReferenceGeometry> SettingsCrossAxisBranches = new List<CrossAxisReferenceGeometry>();
            internal readonly List<VerticalLayoutGroup> SettingsVerticalLayouts = new List<VerticalLayoutGroup>();
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
            internal float FormationRailWidth;
            internal float FormationLeftMargin;
            internal float FormationTopMargin;
            internal float FormationBottomMargin;
            internal float FormationDropGap;
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

            RectTransform serializedDropZone = squadMaker != null && squadMaker.DropZone != null
                ? squadMaker.DropZone.transform as RectTransform
                : null;
            reference.DropZone = FindDirectChildAncestor(serializedDropZone, squadComposition);
            reference.Header = CaptureHeaderReference(squadMaker, squadComposition);

            if (reference.SettingsLayout == null)
            {
                CaptureNormalizedHorizontalChildren(squadSettings, reference.SettingsChildren);
            }

            CaptureSettingsCrossAxisRelationships(squadSettings, reference);
            CaptureSettingsOverlay(squadSettings, squadMaker != null ? squadMaker.ShipInfoBox : null, reference);
            CaptureSettingsOverlay(squadSettings, squadMaker != null ? squadMaker.SquadInfoBox : null, reference);

            CaptureNormalizedHorizontalChildren(
                squadComposition,
                reference.CompositionChildren,
                reference.Formations,
                reference.ActionRow,
                reference.DropZone,
                reference.Header != null && reference.Header.Owner != squadComposition ? reference.Header.Owner : null,
                reference.Header != null ? reference.Header.Supply : null,
                reference.Header != null ? reference.Header.Name : null,
                reference.Header != null ? reference.Header.Color : null,
                reference.Header != null ? reference.Header.Count : null);

            if (reference.DropZone != null)
            {
                reference.DropZoneMargins = CaptureMargins(squadComposition, reference.DropZone);
            }
            CaptureFormationRail(reference);
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
            ConfigureFormationRailAndDropZone(reference);
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

            NestedLayoutReference reference = new NestedLayoutReference { Owner = owner, Layout = layout };
            for (int i = 0; i < owner.childCount; i++)
            {
                RectTransform child = owner.GetChild(i) as RectTransform;
                if (child != null)
                {
                    reference.Children.Add(new LayoutChildReference
                    {
                        Rect = child,
                        Width = Mathf.Abs(child.rect.width * child.localScale.x)
                    });
                }
            }
            return reference;
        }

        private static void ApplyNestedLayout(NestedLayoutReference reference)
        {
            if (reference == null || reference.Owner == null || reference.Layout == null || !reference.Layout.enabled)
            {
                return;
            }

            HorizontalLayoutGroup horizontal = reference.Layout as HorizontalLayoutGroup;
            if (horizontal != null)
            {
                horizontal.childControlWidth = true;
                horizontal.childForceExpandWidth = false;
                float total = 0f;
                for (int i = 0; i < reference.Children.Count; i++)
                {
                    if (reference.Children[i]?.Rect != null)
                    {
                        total += reference.Children[i].Width;
                    }
                }
                float available = Mathf.Max(
                    0f,
                    reference.Owner.rect.width - horizontal.padding.left - horizontal.padding.right -
                    Mathf.Max(0, reference.Children.Count - 1) * horizontal.spacing);
                for (int i = 0; i < reference.Children.Count; i++)
                {
                    LayoutChildReference child = reference.Children[i];
                    if (child?.Rect == null || child.Width <= 0f)
                    {
                        continue;
                    }
                    float width = total > GeometryTolerance ? available * child.Width / total : child.Width;
                    LayoutElement element = GetOrAddLayoutElement(child.Rect);
                    element.ignoreLayout = false;
                    element.minWidth = width;
                    element.preferredWidth = width;
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

        private static HeaderReferenceGeometry CaptureHeaderReference(SquadMaker squadMaker, RectTransform composition)
        {
            if (squadMaker == null || composition == null)
            {
                return null;
            }

            RectTransform supplyLeaf = squadMaker.SquadMakerSupplyCapacityLabel != null
                ? squadMaker.SquadMakerSupplyCapacityLabel.transform as RectTransform : null;
            RectTransform nameLeaf = squadMaker.SquadNameInput != null
                ? squadMaker.SquadNameInput.transform as RectTransform : null;
            RectTransform colorLeaf = squadMaker.SquadColorPickerButton != null
                ? squadMaker.SquadColorPickerButton.transform as RectTransform : null;
            RectTransform countLeaf = squadMaker.SquadShipCount != null
                ? squadMaker.SquadShipCount.transform as RectTransform : null;

            if (!IsDescendantOrSelf(supplyLeaf, composition) ||
                !IsDescendantOrSelf(nameLeaf, composition) ||
                !IsDescendantOrSelf(colorLeaf, composition) ||
                !IsDescendantOrSelf(countLeaf, composition))
            {
                return null;
            }

            RectTransform owner = FindLowestCommonAncestorWithin(composition, supplyLeaf, nameLeaf, colorLeaf, countLeaf);
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
                supply = supplyLeaf;
                name = nameLeaf;
                color = colorLeaf;
                count = countLeaf;
            }

            Rect supplyBounds = CalculateRectBounds(owner, supply);
            Rect nameBounds = CalculateRectBounds(owner, name);
            Rect colorBounds = CalculateRectBounds(owner, color);
            Rect countBounds = CalculateRectBounds(owner, count);
            if (supplyBounds.xMin > nameBounds.xMin || nameBounds.xMin > colorBounds.xMin || colorBounds.xMin > countBounds.xMin)
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
                reference.Supply == null || reference.Name == null || reference.Color == null || reference.Count == null ||
                reference.OwnerWidth <= GeometryTolerance)
            {
                return;
            }

            if (reference.Owner != reference.OuterOwner)
            {
                float available = Mathf.Max(
                    0f,
                    reference.OuterOwner.rect.width - reference.OwnerLeftMargin - reference.OwnerRightMargin);
                SetHorizontalBoundsInOwner(
                    reference.OuterOwner,
                    reference.Owner,
                    reference.OuterOwner.rect.xMin + reference.OwnerLeftMargin,
                    available);
            }

            float liveWidth = Mathf.Abs(CalculateRectBounds(reference.OuterOwner, reference.Owner).width);
            if (liveWidth <= GeometryTolerance)
            {
                return;
            }

            float ratio = liveWidth / reference.OwnerWidth;
            float compactScale = Mathf.Min(1f, ratio);
            float nameScale = ratio < 1f ? ratio : Mathf.Min(MaximumNameWidthScale, ratio);

            float supplyWidth = reference.SupplyWidth * compactScale;
            float nameWidth = reference.NameWidth * nameScale;
            float colorWidth = reference.ColorWidth * compactScale;
            float countWidth = reference.CountWidth * compactScale;

            float left = reference.LeftMargin * compactScale;
            float supplyName = reference.SupplyNameGap * compactScale;
            float nameColor = reference.NameColorGap * compactScale;
            float colorCount = reference.ColorCountGap * compactScale;
            float right = reference.RightMargin * compactScale;

            float occupied = supplyWidth + nameWidth + colorWidth + countWidth +
                left + supplyName + nameColor + colorCount + right;
            float surplus = Mathf.Max(0f, liveWidth - occupied);
            float weightTotal =
                Mathf.Max(1f, reference.LeftMargin) +
                Mathf.Max(1f, reference.SupplyNameGap) +
                Mathf.Max(1f, reference.NameColorGap) +
                Mathf.Max(1f, reference.ColorCountGap) +
                Mathf.Max(1f, reference.RightMargin);

            if (surplus > 0f)
            {
                left += surplus * Mathf.Max(1f, reference.LeftMargin) / weightTotal;
                supplyName += surplus * Mathf.Max(1f, reference.SupplyNameGap) / weightTotal;
                nameColor += surplus * Mathf.Max(1f, reference.NameColorGap) / weightTotal;
                colorCount += surplus * Mathf.Max(1f, reference.ColorCountGap) / weightTotal;
                right += surplus * Mathf.Max(1f, reference.RightMargin) / weightTotal;
            }

            Rect ownerBounds = CalculateRectBounds(reference.Owner, reference.Owner);
            float cursor = ownerBounds.xMin + left;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Supply, cursor, supplyWidth);
            cursor += supplyWidth + supplyName;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Name, cursor, nameWidth);
            cursor += nameWidth + nameColor;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Color, cursor, colorWidth);
            cursor += colorWidth + colorCount;
            SetHorizontalBoundsInOwner(reference.Owner, reference.Count, cursor, countWidth);
            StretchNestedInputVisual(reference.Name);
        }

        private static void StretchNestedInputVisual(RectTransform headerNameOwner)
        {
            if (headerNameOwner == null)
            {
                return;
            }
            TMP_InputField input = headerNameOwner.GetComponent<TMP_InputField>() ??
                headerNameOwner.GetComponentInChildren<TMP_InputField>(true);
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

        private static void CaptureFormationRail(ReferenceGeometry reference)
        {
            if (reference?.Composition == null || reference.Formations == null)
            {
                return;
            }
            Rect formation = CalculateRectBounds(reference.Composition, reference.Formations);
            reference.FormationRailWidth = Mathf.Max(0f, formation.width);
            reference.FormationLeftMargin = Mathf.Max(0f, formation.xMin - reference.Composition.rect.xMin);
            reference.FormationTopMargin = Mathf.Max(0f, reference.Composition.rect.yMax - formation.yMax);
            reference.FormationBottomMargin = Mathf.Max(0f, formation.yMin - reference.Composition.rect.yMin);

            if (reference.DropZone != null)
            {
                Rect drop = CalculateRectBounds(reference.Composition, reference.DropZone);
                reference.FormationDropGap = Mathf.Max(OverlayGap, drop.xMin - formation.xMax);
            }
            else
            {
                reference.FormationDropGap = OverlayGap;
            }
        }

        private static void ConfigureFormationRailAndDropZone(ReferenceGeometry reference)
        {
            RectTransform composition = reference?.Composition;
            if (composition == null)
            {
                return;
            }

            float compositionWidth = Mathf.Abs(composition.rect.width);
            float railWidth = Mathf.Min(
                reference.FormationRailWidth,
                Mathf.Max(0f, compositionWidth * 0.20f));
            float railLeft = reference.FormationLeftMargin;

            if (reference.Formations != null)
            {
                SetHorizontalBoundsInOwner(
                    composition,
                    reference.Formations,
                    composition.rect.xMin + railLeft,
                    railWidth);
                StretchVertical(reference.Formations, reference.FormationBottomMargin, reference.FormationTopMargin);
                ConstrainDirectChildrenHorizontally(reference.Formations);
            }

            if (reference.DropZone != null)
            {
                float reservedLeft = railLeft + railWidth + reference.FormationDropGap;
                float left = Mathf.Max(reference.DropZoneMargins.Left, reservedLeft);
                float maximumLeft = Mathf.Max(
                    0f,
                    compositionWidth - reference.DropZoneMargins.Right - 1f);
                left = Mathf.Min(left, maximumLeft);

                reference.DropZone.anchorMin = Vector2.zero;
                reference.DropZone.anchorMax = Vector2.one;
                reference.DropZone.offsetMin = new Vector2(left, reference.DropZoneMargins.Bottom);
                reference.DropZone.offsetMax = new Vector2(
                    -reference.DropZoneMargins.Right,
                    -reference.DropZoneMargins.Top);
            }
        }

        private static void ConstrainDirectChildrenHorizontally(RectTransform owner)
        {
            if (owner == null)
            {
                return;
            }
            for (int i = 0; i < owner.childCount; i++)
            {
                RectTransform child = owner.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf)
                {
                    continue;
                }
                Rect bounds = CalculateRectBounds(owner, child);
                float width = Mathf.Min(bounds.width, Mathf.Abs(owner.rect.width));
                float left = Mathf.Clamp(
                    bounds.xMin,
                    owner.rect.xMin,
                    owner.rect.xMax - width);
                SetHorizontalBoundsInOwner(owner, child, left, width);
            }
        }

        private static void ConfigureSquadListRows(RectTransform listRoot)
        {
            if (listRoot == null)
            {
                return;
            }

            for (int i = 0; i < listRoot.childCount; i++)
            {
                RectTransform row = listRoot.GetChild(i) as RectTransform;
                if (row == null)
                {
                    continue;
                }
                TMP_Text label = FindSquadRowLabel(row);
                if (label == null)
                {
                    continue;
                }

                HorizontalLayoutGroup authoredLayout = row.GetComponent<HorizontalLayoutGroup>();
                RectOffset padding = authoredLayout != null ? authoredLayout.padding : null;
                float leftPadding = padding != null ? padding.left : 0f;
                float rightPadding = padding != null ? padding.right : 0f;
                float spacing = authoredLayout != null
                    ? authoredLayout.spacing
                    : Mathf.Max(4f, Mathf.Abs(row.rect.height) * 0.15f);
                if (authoredLayout != null && authoredLayout.enabled)
                {
                    authoredLayout.enabled = false;
                }

                RectTransform runtimeIcon = FindDirectChildByName(row, RuntimeIconContainerName);
                RectTransform legacyIcon = FindDirectChildByName(row, LegacySquadIconName);
                float slotWidth = legacyIcon != null
                    ? Mathf.Abs(legacyIcon.rect.width * legacyIcon.localScale.x)
                    : runtimeIcon != null ? Mathf.Abs(runtimeIcon.rect.width * runtimeIcon.localScale.x) : 0f;
                float slotHeight = Mathf.Min(
                    Mathf.Abs(row.rect.height),
                    legacyIcon != null
                        ? Mathf.Abs(legacyIcon.rect.height * legacyIcon.localScale.y)
                        : Mathf.Abs(row.rect.height));

                if (runtimeIcon != null && legacyIcon != null)
                {
                    legacyIcon.gameObject.SetActive(false);
                }

                RectTransform iconSlot = runtimeIcon != null ? runtimeIcon : legacyIcon;
                if (iconSlot != null && iconSlot.gameObject.activeSelf && slotWidth > GeometryTolerance)
                {
                    SetHorizontalBoundsInOwner(
                        row,
                        iconSlot,
                        row.rect.xMin + leftPadding,
                        slotWidth);
                    SetVerticalCenterAndHeight(row, iconSlot, slotHeight);
                }

                float labelLeft = leftPadding + slotWidth + (slotWidth > GeometryTolerance ? spacing : 0f);
                RectTransform labelRect = label.rectTransform;
                Vector2 anchorMin = labelRect.anchorMin;
                Vector2 anchorMax = labelRect.anchorMax;
                Vector2 offsetMin = labelRect.offsetMin;
                Vector2 offsetMax = labelRect.offsetMax;
                anchorMin.x = 0f;
                anchorMax.x = 1f;
                offsetMin.x = labelLeft;
                offsetMax.x = -rightPadding;
                labelRect.anchorMin = anchorMin;
                labelRect.anchorMax = anchorMax;
                labelRect.offsetMin = offsetMin;
                labelRect.offsetMax = offsetMax;
                label.horizontalAlignment = HorizontalAlignmentOptions.Left;
            }
        }

        private static TMP_Text FindSquadRowLabel(RectTransform row)
        {
            Transform exact = row != null ? row.Find(SquadNameObjectName) : null;
            TMP_Text label = exact != null ? exact.GetComponent<TMP_Text>() : null;
            if (label != null)
            {
                return label;
            }
            exact = row != null ? row.Find(SquadNumberObjectName) : null;
            label = exact != null ? exact.GetComponent<TMP_Text>() : null;
            if (label != null)
            {
                return label;
            }
            TMP_Text[] labels = row != null ? row.GetComponentsInChildren<TMP_Text>(true) : Array.Empty<TMP_Text>();
            for (int i = 0; i < labels.Length; i++)
            {
                string text = labels[i] != null ? labels[i].text : null;
                if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("Squad", StringComparison.OrdinalIgnoreCase))
                {
                    return labels[i];
                }
            }
            return null;
        }

        private static void CaptureActionRowMetrics(ReferenceGeometry reference)
        {
            RectTransform row = reference?.ActionRow;
            if (row == null)
            {
                return;
            }
            List<Rect> children = new List<Rect>();
            float maxHeight = 0f;
            for (int i = 0; i < row.childCount; i++)
            {
                RectTransform child = row.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }
                Rect bounds = CalculateRectBounds(row, child);
                children.Add(bounds);
                maxHeight = Mathf.Max(maxHeight, bounds.height);
            }
            children.Sort((a, b) => a.center.x.CompareTo(b.center.x));
            float gapTotal = 0f;
            int gapCount = 0;
            for (int i = 1; i < children.Count; i++)
            {
                float gap = children[i].xMin - children[i - 1].xMax;
                if (gap >= 0f)
                {
                    gapTotal += gap;
                    gapCount++;
                }
            }
            reference.ActionRowHeight = maxHeight > GeometryTolerance ? maxHeight : Mathf.Abs(row.rect.height);
            reference.ActionSpacing = gapCount > 0 ? gapTotal / gapCount : 0f;
        }

        private static void ConfigureActionRow(ReferenceGeometry reference)
        {
            RectTransform row = reference?.ActionRow;
            if (row == null)
            {
                return;
            }
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0.5f, 0f);
            row.anchoredPosition = Vector2.zero;
            row.sizeDelta = new Vector2(0f, reference.ActionRowHeight);
            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>() ?? row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = reference.ActionSpacing;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            LayoutRebuilder.ForceRebuildLayoutImmediate(row);
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
            float width = Mathf.Abs(owner.rect.width);
            if (width <= GeometryTolerance)
            {
                return;
            }
            for (int i = 0; i < owner.childCount; i++)
            {
                RectTransform child = owner.GetChild(i) as RectTransform;
                if (child == null || IsExcluded(child, excluded))
                {
                    continue;
                }
                Rect bounds = CalculateRectBounds(owner, child);
                destination.Add(new HorizontalReferenceGeometry
                {
                    Rect = child,
                    MinFraction = Mathf.Clamp01((bounds.xMin - owner.rect.xMin) / width),
                    MaxFraction = Mathf.Clamp01((bounds.xMax - owner.rect.xMin) / width)
                });
            }
        }

        private static void ApplyNormalizedHorizontalGeometry(List<HorizontalReferenceGeometry> references)
        {
            if (references == null)
            {
                return;
            }
            for (int i = 0; i < references.Count; i++)
            {
                HorizontalReferenceGeometry reference = references[i];
                RectTransform rect = reference?.Rect;
                if (rect == null)
                {
                    continue;
                }
                Vector2 min = rect.anchorMin;
                Vector2 max = rect.anchorMax;
                Vector2 pos = rect.anchoredPosition;
                Vector2 size = rect.sizeDelta;
                min.x = reference.MinFraction;
                max.x = reference.MaxFraction;
                pos.x = 0f;
                size.x = 0f;
                rect.anchorMin = min;
                rect.anchorMax = max;
                rect.anchoredPosition = pos;
                rect.sizeDelta = size;
            }
        }

        private static void CaptureSettingsCrossAxisRelationships(RectTransform settings, ReferenceGeometry reference)
        {
            CaptureSettingsNode(settings, reference, 0);
        }

        private static void CaptureSettingsNode(RectTransform current, ReferenceGeometry reference, int depth)
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
            for (int i = 0; i < current.childCount; i++)
            {
                RectTransform child = current.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }
                LayoutElement element = child.GetComponent<LayoutElement>();
                bool layoutOwns = layoutEnabled && (element == null || !element.ignoreLayout);
                if (!layoutOwns && ownerWidth > GeometryTolerance)
                {
                    Rect bounds = CalculateRectBounds(current, child);
                    if (Mathf.Abs(bounds.width) / ownerWidth >= SettingsStructuralCrossAxisCoverage)
                    {
                        if (child.GetComponent<Image>() != null)
                        {
                            reference.SettingsCrossAxisBranches.Add(new CrossAxisReferenceGeometry { Rect = child });
                        }
                        else
                        {
                            AddCrossAxisReference(reference.SettingsCrossAxisBranches, current, child, bounds);
                        }
                    }
                }
                CaptureSettingsNode(child, reference, depth + 1);
            }
        }

        private static void CaptureSettingsOverlay(RectTransform settings, GameObject overlayObject, ReferenceGeometry reference)
        {
            if (settings == null || overlayObject == null || reference == null)
            {
                return;
            }
            RectTransform branch = FindDirectChildAncestor(overlayObject.transform as RectTransform, settings);
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
            for (int i = 0; i < reference.SettingsVerticalLayouts.Count; i++)
            {
                VerticalLayoutGroup layout = reference.SettingsVerticalLayouts[i];
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
            for (int i = 0; i < reference.SettingsOverlays.Count; i++)
            {
                RectTransform overlay = reference.SettingsOverlays[i];
                if (overlay == null)
                {
                    continue;
                }
                GetOrAddLayoutElement(overlay).ignoreLayout = true;
                StretchHorizontal(overlay, 0f, 0f);
            }
        }

        private static void CaptureColumnCrossAxisRelationships(SquadMaker squadMaker, ReferenceGeometry reference)
        {
            if (squadMaker == null || reference == null || squadMaker.ChosenSquadList == null)
            {
                return;
            }
            RectTransform chosenList = squadMaker.ChosenSquadList.transform as RectTransform;
            RectTransform chosenColumn = FindAncestorByName(chosenList, ChosenSquadsColumnName);
            RectTransform squadsColumn = FindAncestorByName(chosenColumn, SquadsColumnName);
            RectTransform mainContainer = FindAncestorByName(squadsColumn, MainContainerName);
            CaptureColumnRoot(FindDirectChildByName(mainContainer, ShipSelectorColumnName), reference);
            CaptureColumnRoot(FindDirectChildByName(squadsColumn, SavedSquadsColumnName), reference);
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

        private static void CaptureColumnNode(RectTransform current, ReferenceGeometry reference, int depth)
        {
            if (current == null || reference == null || depth >= MaxColumnTraversalDepth)
            {
                return;
            }
            LayoutGroup ownerLayout = current.GetComponent<LayoutGroup>();
            bool layoutOwns = ownerLayout != null && ownerLayout.enabled;
            VerticalLayoutGroup vertical = ownerLayout as VerticalLayoutGroup;
            if (vertical != null && !reference.ColumnVerticalLayouts.Contains(vertical))
            {
                reference.ColumnVerticalLayouts.Add(vertical);
            }
            float ownerWidth = Mathf.Abs(current.rect.width);
            for (int i = 0; i < current.childCount; i++)
            {
                RectTransform child = current.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }
                if (!layoutOwns && ownerWidth > GeometryTolerance)
                {
                    Rect bounds = CalculateRectBounds(current, child);
                    if (Mathf.Abs(bounds.width) / ownerWidth >= StructuralCrossAxisCoverage)
                    {
                        AddCrossAxisReference(reference.ColumnCrossAxisBranches, current, child, bounds);
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
            for (int i = 0; i < reference.ColumnVerticalLayouts.Count; i++)
            {
                VerticalLayoutGroup layout = reference.ColumnVerticalLayouts[i];
                if (layout != null && layout.enabled)
                {
                    layout.childControlWidth = true;
                    layout.childForceExpandWidth = true;
                }
            }
            ApplyCrossAxisReferences(reference.ColumnCrossAxisBranches);
            for (int i = 0; i < reference.ColumnRoots.Count; i++)
            {
                if (reference.ColumnRoots[i] != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(reference.ColumnRoots[i]);
                }
            }
        }

        private static void AddCrossAxisReference(
            List<CrossAxisReferenceGeometry> destination,
            RectTransform owner,
            RectTransform child,
            Rect bounds)
        {
            destination.Add(new CrossAxisReferenceGeometry
            {
                Rect = child,
                LeftMargin = Mathf.Max(0f, bounds.xMin - owner.rect.xMin),
                RightMargin = Mathf.Max(0f, owner.rect.xMax - bounds.xMax)
            });
        }

        private static void ApplyCrossAxisReferences(List<CrossAxisReferenceGeometry> references)
        {
            if (references == null)
            {
                return;
            }
            for (int i = 0; i < references.Count; i++)
            {
                CrossAxisReferenceGeometry reference = references[i];
                if (reference?.Rect != null)
                {
                    StretchHorizontal(reference.Rect, reference.LeftMargin, reference.RightMargin);
                }
            }
        }

        private static void CenterHeading(RectTransform owner, string heading)
        {
            if (owner == null)
            {
                return;
            }
            TMP_Text[] labels = owner.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                TMP_Text label = labels[i];
                if (label == null || !string.Equals(label.text?.Trim(), heading, StringComparison.Ordinal))
                {
                    continue;
                }
                RectTransform rect = label.rectTransform;
                RectTransform parent = rect != null ? rect.parent as RectTransform : null;
                LayoutGroup parentLayout = parent != null ? parent.GetComponent<LayoutGroup>() : null;
                LayoutElement element = rect != null ? rect.GetComponent<LayoutElement>() : null;
                bool layoutOwns = parentLayout != null && parentLayout.enabled && (element == null || !element.ignoreLayout);
                if (!layoutOwns && rect != null)
                {
                    StretchHorizontal(rect, 0f, 0f);
                }
                label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            }
        }

        private static void ConfigureColorPickerPlacement(SquadMaker squadMaker)
        {
            if (squadMaker == null || squadMaker.ColorPicker == null || squadMaker.SquadColorPickerButton == null)
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
            SquadMakerColorPickerPlacementRelay relay = picker.GetComponent<SquadMakerColorPickerPlacementRelay>();
            if (relay == null)
            {
                relay = picker.gameObject.AddComponent<SquadMakerColorPickerPlacementRelay>();
            }
            relay.Configure(viewport, anchor);
        }

        internal static bool PositionOverlayNearAnchor(RectTransform viewport, RectTransform anchor, RectTransform overlay)
        {
            if (viewport == null || anchor == null || overlay == null)
            {
                return false;
            }
            Rect anchorBounds = CalculateRectBounds(viewport, anchor);
            Rect overlayBounds = CalculateRectBounds(viewport, overlay);
            if (overlayBounds.width <= GeometryTolerance || overlayBounds.height <= GeometryTolerance)
            {
                return false;
            }
            Rect available = viewport.rect;
            Vector2 correction = new Vector2(
                anchorBounds.center.x - overlayBounds.center.x,
                (anchorBounds.yMin - OverlayGap) - overlayBounds.yMax);
            if (overlayBounds.yMin + correction.y < available.yMin)
            {
                float above = (anchorBounds.yMax + OverlayGap) - overlayBounds.yMin;
                if (overlayBounds.yMax + above <= available.yMax)
                {
                    correction.y = above;
                }
            }
            MoveRectByOwnerDelta(viewport, overlay, correction);
            Rect rendered = CalculateRectBounds(viewport, overlay);
            Vector2 clamp = Vector2.zero;
            if (rendered.width <= available.width)
            {
                if (rendered.xMin < available.xMin) clamp.x = available.xMin - rendered.xMin;
                else if (rendered.xMax > available.xMax) clamp.x = available.xMax - rendered.xMax;
            }
            else clamp.x = available.center.x - rendered.center.x;
            if (rendered.height <= available.height)
            {
                if (rendered.yMin < available.yMin) clamp.y = available.yMin - rendered.yMin;
                else if (rendered.yMax > available.yMax) clamp.y = available.yMax - rendered.yMax;
            }
            else clamp.y = available.center.y - rendered.center.y;
            MoveRectByOwnerDelta(viewport, overlay, clamp);
            return correction.sqrMagnitude > GeometryTolerance || clamp.sqrMagnitude > GeometryTolerance;
        }

        private static void StretchHorizontal(RectTransform rect, float leftMargin, float rightMargin)
        {
            if (rect == null) return;
            Vector2 min = rect.anchorMin;
            Vector2 max = rect.anchorMax;
            Vector2 offMin = rect.offsetMin;
            Vector2 offMax = rect.offsetMax;
            min.x = 0f;
            max.x = 1f;
            offMin.x = leftMargin;
            offMax.x = -rightMargin;
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offMin;
            rect.offsetMax = offMax;
        }

        private static void StretchVertical(RectTransform rect, float bottomMargin, float topMargin)
        {
            if (rect == null) return;
            Vector2 min = rect.anchorMin;
            Vector2 max = rect.anchorMax;
            Vector2 offMin = rect.offsetMin;
            Vector2 offMax = rect.offsetMax;
            min.y = 0f;
            max.y = 1f;
            offMin.y = bottomMargin;
            offMax.y = -topMargin;
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offMin;
            rect.offsetMax = offMax;
        }

        private static void SetVerticalCenterAndHeight(RectTransform owner, RectTransform rect, float height)
        {
            if (owner == null || rect == null) return;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(0f, height));
            Rect bounds = CalculateRectBounds(owner, rect);
            MoveRectByOwnerDelta(owner, rect, new Vector2(0f, owner.rect.center.y - bounds.center.y));
        }

        private static void SetHorizontalBoundsInOwner(RectTransform owner, RectTransform rect, float left, float width)
        {
            if (owner == null || rect == null) return;
            Rect current = CalculateRectBounds(owner, rect);
            float target = Mathf.Max(0f, width);
            if (current.width > GeometryTolerance && Mathf.Abs(rect.rect.width) > GeometryTolerance)
            {
                rect.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Horizontal,
                    Mathf.Abs(rect.rect.width) * target / current.width);
            }
            else
            {
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, target);
            }
            current = CalculateRectBounds(owner, rect);
            MoveRectByOwnerDelta(owner, rect, new Vector2(left - current.xMin, 0f));
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

        private static void MoveRectByOwnerDelta(RectTransform owner, RectTransform rect, Vector2 ownerDelta)
        {
            if (owner == null || rect == null || ownerDelta.sqrMagnitude <= GeometryTolerance) return;
            rect.position += owner.TransformVector(new Vector3(ownerDelta.x, ownerDelta.y, 0f));
        }

        private static Rect CalculateRectBounds(RectTransform owner, RectTransform rect)
        {
            if (owner == null || rect == null) return default;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector3 first = owner.InverseTransformPoint(corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 local = owner.InverseTransformPoint(corners[i]);
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
            if (element == null && rect != null) element = rect.gameObject.AddComponent<LayoutElement>();
            return element;
        }

        private static bool HasEnabledLayoutGroup(RectTransform owner)
        {
            LayoutGroup layout = owner != null ? owner.GetComponent<LayoutGroup>() : null;
            return layout != null && layout.enabled;
        }

        private static bool IsExcluded(RectTransform rect, RectTransform[] excluded)
        {
            if (excluded == null) return false;
            for (int i = 0; i < excluded.Length; i++) if (excluded[i] == rect) return true;
            return false;
        }

        private static bool IsDescendantOrSelf(RectTransform descendant, RectTransform owner)
        {
            Transform current = descendant;
            while (current != null)
            {
                if (current == owner) return true;
                current = current.parent;
            }
            return false;
        }

        private static RectTransform FindLowestCommonAncestorWithin(RectTransform boundary, params RectTransform[] rects)
        {
            if (boundary == null || rects == null || rects.Length == 0 || rects[0] == null) return null;
            RectTransform candidate = rects[0];
            while (candidate != null)
            {
                bool all = true;
                for (int i = 1; i < rects.Length; i++)
                {
                    if (!IsDescendantOrSelf(rects[i], candidate)) { all = false; break; }
                }
                if (all) return candidate;
                if (candidate == boundary) break;
                candidate = candidate.parent as RectTransform;
            }
            return null;
        }

        private static RectTransform ResolveHeaderBranch(RectTransform leaf, RectTransform owner)
        {
            if (leaf == null || owner == null) return null;
            return leaf == owner ? leaf : FindDirectChildAncestor(leaf, owner);
        }

        private static bool AreDistinctHeaderBranches(RectTransform a, RectTransform b, RectTransform c, RectTransform d)
        {
            return a != null && b != null && c != null && d != null &&
                a != b && a != c && a != d && b != c && b != d && c != d;
        }

        private static RectTransform FindAncestorByName(RectTransform start, string name)
        {
            RectTransform current = start;
            while (current != null)
            {
                if (current.name == name) return current;
                current = current.parent as RectTransform;
            }
            return null;
        }

        private static RectTransform FindDirectChildByName(RectTransform owner, string name)
        {
            if (owner == null) return null;
            for (int i = 0; i < owner.childCount; i++)
            {
                RectTransform child = owner.GetChild(i) as RectTransform;
                if (child != null && child.name == name) return child;
            }
            return null;
        }

        private static RectTransform FindDirectChildAncestor(RectTransform descendant, RectTransform owner)
        {
            if (descendant == null || owner == null) return null;
            RectTransform current = descendant;
            while (current != null && current.parent != owner) current = current.parent as RectTransform;
            return current != null && current.parent == owner ? current : null;
        }
    }

    internal sealed class SquadMakerColorPickerPlacementRelay : MonoBehaviour
    {
        private RectTransform _viewport;
        private RectTransform _anchor;
        private RectTransform _overlay;
        private ColorPicker _picker;

        internal void Configure(RectTransform viewport, RectTransform anchor)
        {
            _viewport = viewport;
            _anchor = anchor;
            _overlay = transform as RectTransform;
            _picker = GetComponent<ColorPicker>();
            if (isActiveAndEnabled) Reposition();
        }

        private void OnEnable() { Reposition(); }
        private void LateUpdate() { Reposition(); }

        private void Reposition()
        {
            if (_picker != null) _picker.PrepareResponsiveGeometry();
            SquadMakerCompositionLayoutGuard.PositionOverlayNearAnchor(_viewport, _anchor, _overlay);
        }
    }
}
