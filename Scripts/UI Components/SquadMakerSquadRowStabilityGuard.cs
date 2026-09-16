using Assets.Scripts.Scenes;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Applies the final squad-row layout in the same frame a row is recreated after drag/drop.
    /// SquadMaker creates the replacement row with worldPositionStays enabled, while the responsive
    /// layout guard normally corrects it on a later pass; that one-frame gap is the visible left jump.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class SquadMakerSquadRowStabilityGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string RuntimeIconContainerName = "Icon Container";
        private const string LegacySquadIconName = "Squad Icon";
        private SquadMaker _squadMaker;

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

            SquadMaker squadMaker = FindObjectOfType<SquadMaker>();
            if (squadMaker == null)
            {
                return;
            }

            SquadMakerSquadRowStabilityGuard guard = squadMaker.GetComponent<SquadMakerSquadRowStabilityGuard>();
            if (guard == null)
            {
                guard = squadMaker.gameObject.AddComponent<SquadMakerSquadRowStabilityGuard>();
            }
            guard._squadMaker = squadMaker;
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                _squadMaker = GetComponent<SquadMaker>();
            }
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            StabilizeList(_squadMaker.SavedSquadList);
            StabilizeList(_squadMaker.ChosenSquadList);
        }

        private static void StabilizeList(GameObject listObject)
        {
            RectTransform list = listObject != null ? listObject.transform as RectTransform : null;
            if (list == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(list);

            for (int i = 0; i < list.childCount; i++)
            {
                RectTransform row = list.GetChild(i) as RectTransform;
                if (row == null)
                {
                    continue;
                }

                row.localScale = Vector3.one;
                TMP_Text label = FindSquadLabel(row);
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

                RectTransform runtimeIcon = FindDirectChild(row, RuntimeIconContainerName);
                RectTransform legacyIcon = FindDirectChild(row, LegacySquadIconName);
                RectTransform icon = runtimeIcon != null ? runtimeIcon : legacyIcon;
                float iconWidth = icon != null && icon.gameObject.activeSelf
                    ? Mathf.Abs(icon.rect.width * icon.localScale.x)
                    : 0f;

                RectTransform labelRect = label.rectTransform;
                Vector2 anchorMin = labelRect.anchorMin;
                Vector2 anchorMax = labelRect.anchorMax;
                Vector2 offsetMin = labelRect.offsetMin;
                Vector2 offsetMax = labelRect.offsetMax;
                anchorMin.x = 0f;
                anchorMax.x = 1f;
                offsetMin.x = leftPadding + iconWidth + (iconWidth > 0.001f ? spacing : 0f);
                offsetMax.x = -rightPadding;
                labelRect.anchorMin = anchorMin;
                labelRect.anchorMax = anchorMax;
                labelRect.offsetMin = offsetMin;
                labelRect.offsetMax = offsetMax;
                label.horizontalAlignment = HorizontalAlignmentOptions.Left;
            }
        }

        private static RectTransform FindDirectChild(RectTransform row, string name)
        {
            for (int i = 0; i < row.childCount; i++)
            {
                Transform child = row.GetChild(i);
                if (child != null && string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child as RectTransform;
                }
            }
            return null;
        }

        private static TMP_Text FindSquadLabel(RectTransform row)
        {
            Transform exact = row.Find("Squad Name");
            TMP_Text label = exact != null ? exact.GetComponent<TMP_Text>() : null;
            if (label != null)
            {
                return label;
            }

            exact = row.Find("Squad Number");
            label = exact != null ? exact.GetComponent<TMP_Text>() : null;
            if (label != null)
            {
                return label;
            }

            TMP_Text[] labels = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                string text = labels[i] != null ? labels[i].text : null;
                if (!string.IsNullOrWhiteSpace(text) &&
                    text.TrimStart().StartsWith("Squad", StringComparison.OrdinalIgnoreCase))
                {
                    return labels[i];
                }
            }
            return null;
        }
    }
}
