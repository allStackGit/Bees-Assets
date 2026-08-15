using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Adds a transparent full-canvas raycast target behind a modal's visible panel. The modal's
    /// own controls remain on top while pointer events can no longer fall through to menu/HUD
    /// controls around the edges of the panel.
    /// </summary>
    public static class ModalInputBlocker
    {
        private const string BlockerName = "Modal Input Blocker";

        public static void Ensure(GameObject modalRoot)
        {
            if (modalRoot == null || modalRoot.transform.Find(BlockerName) != null)
            {
                return;
            }

            GameObject blocker = new GameObject(
                BlockerName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            blocker.layer = modalRoot.layer;

            RectTransform rect = blocker.GetComponent<RectTransform>();
            rect.SetParent(modalRoot.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            rect.SetSiblingIndex(0);

            Image image = blocker.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
        }
    }
}
