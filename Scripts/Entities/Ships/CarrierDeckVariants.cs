using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.Entities.Ships
{
    /// <summary>
    /// Resolves optional carrier-deck artwork from a squad color. The deck choice is intentionally
    /// derived from the persisted squad color so existing save/server formats do not need another field.
    /// </summary>
    public static class CarrierDeckVariants
    {
        public const int DeckCount = 13;
        public const int Columns = 7;
        public const int Rows = 2;
        public const int SpriteWidth = 96;
        public const int SpriteHeight = 112;

        private const float PixelsPerUnit = 100f;
        private const float HueRange = 0.035f;
        private const float SaturationRange = 0.18f;
        private const float ValueRange = 0.18f;
        private const float NeutralSaturationRange = 0.10f;
        private const float NeutralValueRange = 0.12f;
        private const string ResourcePath = "Sprites/carrier_alts_deck";
        private const string UiDeckVariantObjectName = "Carrier Deck Variant";

        // Top row left-to-right, then bottom row left-to-right. The fourteenth sheet cell is blank.
        // The ranges are deliberately bounded in hue, saturation, and value so discovering a deck
        // does not require an exact color while unrelated colors still keep the normal carrier deck.
        private static readonly Color32[] MatchColors =
        {
            new Color32(195, 98, 107, 255),
            new Color32(193, 111, 56, 255),
            new Color32(220, 213, 74, 255),
            new Color32(147, 204, 93, 255),
            new Color32(68, 137, 108, 255),
            new Color32(79, 180, 79, 255),
            new Color32(98, 180, 197, 255),
            new Color32(95, 108, 195, 255),
            new Color32(155, 124, 171, 255),
            new Color32(214, 135, 189, 255),
            new Color32(152, 104, 104, 255),
            new Color32(184, 196, 221, 255),
            new Color32(105, 168, 63, 255),
        };

        private static Texture2D _texture;
        private static Sprite[] _sprites;
        private static bool _loadAttempted;

        public static int GetDeckIndex(Color color)
        {
            Color.RGBToHSV(color, out float hue, out float saturation, out float value);
            float bestDistance = float.MaxValue;
            int deckIndex = -1;

            for (int i = 0; i < MatchColors.Length; i++)
            {
                Color anchor = MatchColors[i];
                Color.RGBToHSV(anchor, out float anchorHue, out float anchorSaturation, out float anchorValue);

                float saturationDistance = Mathf.Abs(saturation - anchorSaturation);
                float valueDistance = Mathf.Abs(value - anchorValue);
                float distance;

                if (anchorSaturation < 0.2f)
                {
                    if (saturationDistance > NeutralSaturationRange || valueDistance > NeutralValueRange)
                    {
                        continue;
                    }
                    distance = saturationDistance + valueDistance;
                }
                else
                {
                    float hueDistance = Mathf.Abs(hue - anchorHue);
                    hueDistance = Mathf.Min(hueDistance, 1f - hueDistance);
                    if (hueDistance > HueRange || saturationDistance > SaturationRange || valueDistance > ValueRange)
                    {
                        continue;
                    }

                    distance = hueDistance * 3.2f + saturationDistance * 0.15f + valueDistance * 0.1f;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    deckIndex = i;
                }
            }

            return deckIndex;
        }

        public static bool TryGetDeckIndex(Color color, out int deckIndex)
        {
            deckIndex = GetDeckIndex(color);
            return deckIndex >= 0;
        }

        public static Sprite GetDeckSprite(Color color)
        {
            int deckIndex = GetDeckIndex(color);
            if (deckIndex < 0)
            {
                return null;
            }

            EnsureSpritesLoaded();
            return _sprites != null ? _sprites[deckIndex] : null;
        }

        /// <summary>
        /// Applies a carrier-deck overlay to a UI image without changing the base image sprite.
        /// Passing null hides an existing overlay, allowing recycled/reused UI surfaces to safely
        /// switch between carrier and non-carrier squads.
        /// </summary>
        public static void SetUiDeckVariant(Image baseImage, Sprite deckSprite)
        {
            if (baseImage == null)
            {
                return;
            }

            Transform existing = baseImage.transform.Find(UiDeckVariantObjectName);
            Image overlay = existing != null ? existing.GetComponent<Image>() : null;

            if (deckSprite == null)
            {
                if (overlay != null)
                {
                    overlay.enabled = false;
                }
                return;
            }

            if (overlay == null)
            {
                GameObject deckObject = new GameObject(
                    UiDeckVariantObjectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                deckObject.transform.SetParent(baseImage.transform, false);
                overlay = deckObject.GetComponent<Image>();
            }

            RectTransform rectTransform = overlay.rectTransform;
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.localScale = Vector3.one;
            overlay.transform.SetAsLastSibling();
            overlay.sprite = deckSprite;
            overlay.color = Color.white;
            overlay.raycastTarget = false;
            overlay.preserveAspect = false;
            overlay.enabled = true;
        }

        internal static Rect GetSpriteRect(int deckIndex)
        {
            if (deckIndex < 0 || deckIndex >= DeckCount)
            {
                return Rect.zero;
            }

            int column = deckIndex % Columns;
            int rowFromTop = deckIndex / Columns;
            int rowFromBottom = Rows - 1 - rowFromTop;
            return new Rect(
                column * SpriteWidth,
                rowFromBottom * SpriteHeight,
                SpriteWidth,
                SpriteHeight);
        }

        private static void EnsureSpritesLoaded()
        {
            if (_loadAttempted)
            {
                return;
            }

            _loadAttempted = true;
            TextAsset source = Resources.Load<TextAsset>(ResourcePath);
            if (source == null)
            {
                Debug.LogError($"Carrier deck sprite sheet was not found at Resources/{ResourcePath}.bytes.");
                return;
            }

            _texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "carrier_alts_deck",
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!ImageConversion.LoadImage(_texture, source.bytes, false))
            {
                Debug.LogError("Carrier deck sprite sheet could not be decoded as PNG data.");
                Object.Destroy(_texture);
                _texture = null;
                return;
            }

            int requiredWidth = Columns * SpriteWidth;
            int requiredHeight = Rows * SpriteHeight;
            if (_texture.width != requiredWidth || _texture.height != requiredHeight)
            {
                Debug.LogError(
                    $"Carrier deck sprite sheet must be {requiredWidth}x{requiredHeight}, but was {_texture.width}x{_texture.height}.");
                Object.Destroy(_texture);
                _texture = null;
                return;
            }

            _sprites = new Sprite[DeckCount];
            for (int i = 0; i < DeckCount; i++)
            {
                _sprites[i] = Sprite.Create(
                    _texture,
                    GetSpriteRect(i),
                    new Vector2(0.5f, 0.5f),
                    PixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                _sprites[i].name = $"carrier_alts_deck_{i}";
            }
        }
    }
}
