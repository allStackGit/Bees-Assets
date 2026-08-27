using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Supplies the visual toolbar band when the serialized Squad Maker header controls are direct
    /// children of Squad Composition. Nested header containers remain owned by
    /// SquadMakerCompositionLayoutGuard; this relay does not move or resize existing controls.
    /// </summary>
    [DefaultExecutionOrder(-650)]
    internal sealed class SquadMakerHeaderBackdropGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string BackdropName = "Responsive Squad Header Backdrop";
        private const float GeometryTolerance = 0.001f;

        private RectTransform _composition;
        private RectTransform _supply;
        private RectTransform _name;
        private RectTransform _color;
        private RectTransform _count;
        private RectTransform _backdrop;
        private readonly Vector3[] _corners = new Vector3[4];

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
                if (squadMaker != null)
                {
                    EnsureFor(squadMaker);
                    return;
                }
            }
        }

        internal static SquadMakerHeaderBackdropGuard EnsureFor(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return null;
            }

            RectTransform supply = squadMaker.SquadMakerSupplyCapacityLabel != null
                ? squadMaker.SquadMakerSupplyCapacityLabel.transform as RectTransform
                : null;
            RectTransform name = squadMaker.SquadNameInput != null
                ? squadMaker.SquadNameInput.transform as RectTransform
                : null;
            RectTransform color = squadMaker.SquadColorPickerButton != null
                ? squadMaker.SquadColorPickerButton.transform as RectTransform
                : null;
            RectTransform count = squadMaker.SquadShipCount != null
                ? squadMaker.SquadShipCount.transform as RectTransform
                : null;
            RectTransform composition = FindAncestorByName(name, "Squad Composition");
            if (composition == null || !IsDescendantOrSelf(supply, composition) ||
                !IsDescendantOrSelf(color, composition) || !IsDescendantOrSelf(count, composition))
            {
                return null;
            }

            // A nested common header owner is already handled by SquadMakerCompositionLayoutGuard.
            // The missing case is the real scene shape where the four branches meet only at the
            // composition itself.
            RectTransform commonOwner = FindLowestCommonAncestor(composition, supply, name, color, count);
            if (commonOwner != composition)
            {
                return null;
            }

            SquadMakerHeaderBackdropGuard guard = composition.GetComponent<SquadMakerHeaderBackdropGuard>();
            if (guard == null)
            {
                guard = composition.gameObject.AddComponent<SquadMakerHeaderBackdropGuard>();
            }

            guard.Configure(composition, supply, name, color, count);
            return guard;
        }

        private void Configure(
            RectTransform composition,
            RectTransform supply,
            RectTransform name,
            RectTransform color,
            RectTransform count)
        {
            _composition = composition;
            _supply = supply;
            _name = name;
            _color = color;
            _count = count;
            EnsureBackdrop();
            ApplyBackdrop();
        }

        private void LateUpdate()
        {
            ApplyBackdrop();
        }

        internal void ApplyBackdrop()
        {
            if (_composition == null || _supply == null || _name == null || _color == null || _count == null)
            {
                return;
            }

            EnsureBackdrop();
            if (_backdrop == null)
            {
                return;
            }

            Rect band = CalculateUnionBounds(_composition, _supply, _name, _color, _count);
            if (band.width <= GeometryTolerance || band.height <= GeometryTolerance)
            {
                return;
            }

            float topInset = Mathf.Max(0f, _composition.rect.yMax - band.yMax);
            _backdrop.anchorMin = new Vector2(0f, 1f);
            _backdrop.anchorMax = new Vector2(1f, 1f);
            _backdrop.pivot = new Vector2(0.5f, 1f);
            _backdrop.anchoredPosition = new Vector2(0f, -topInset);
            _backdrop.sizeDelta = new Vector2(0f, band.height);
            _backdrop.SetAsFirstSibling();
        }

        private void EnsureBackdrop()
        {
            if (_backdrop != null || _composition == null)
            {
                return;
            }

            Transform existing = _composition.Find(BackdropName);
            if (existing != null)
            {
                _backdrop = existing as RectTransform;
                return;
            }

            Image source = FindBackdropSource(_supply) ?? FindBackdropSource(_count);
            if (source == null)
            {
                return;
            }

            GameObject backdropObject = new GameObject(
                BackdropName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(LayoutElement));
            _backdrop = backdropObject.GetComponent<RectTransform>();
            _backdrop.SetParent(_composition, false);

            Image backdrop = backdropObject.GetComponent<Image>();
            backdrop.sprite = source.sprite;
            backdrop.color = source.color;
            backdrop.material = source.material;
            backdrop.type = source.type;
            backdrop.preserveAspect = false;
            backdrop.raycastTarget = false;

            LayoutElement layoutElement = backdropObject.GetComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;
            _backdrop.SetAsFirstSibling();
        }

        private static Image FindBackdropSource(RectTransform branch)
        {
            return branch != null
                ? branch.GetComponent<Image>() ?? branch.GetComponentInChildren<Image>(true)
                : null;
        }

        private Rect CalculateUnionBounds(RectTransform owner, params RectTransform[] rects)
        {
            bool found = false;
            float minX = 0f;
            float maxX = 0f;
            float minY = 0f;
            float maxY = 0f;

            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null)
                {
                    continue;
                }

                rect.GetWorldCorners(_corners);
                for (int corner = 0; corner < _corners.Length; corner++)
                {
                    Vector3 local = owner.InverseTransformPoint(_corners[corner]);
                    if (!found)
                    {
                        minX = maxX = local.x;
                        minY = maxY = local.y;
                        found = true;
                    }
                    else
                    {
                        minX = Mathf.Min(minX, local.x);
                        maxX = Mathf.Max(maxX, local.x);
                        minY = Mathf.Min(minY, local.y);
                        maxY = Mathf.Max(maxY, local.y);
                    }
                }
            }

            return found ? Rect.MinMaxRect(minX, minY, maxX, maxY) : default;
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

        private static RectTransform FindLowestCommonAncestor(
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
                for (int i = 1; i < rects.Length; i++)
                {
                    if (!IsDescendantOrSelf(rects[i], candidate))
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

        private static bool IsDescendantOrSelf(RectTransform descendant, RectTransform owner)
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
    }
}
