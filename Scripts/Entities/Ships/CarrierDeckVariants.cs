using UnityEngine;

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
        private const float MatchThreshold = 0.11f;
        private const string ResourcePath = "Sprites/carrier_alts_deck";

        // Top row left-to-right, then bottom row left-to-right. The fourteenth sheet cell is blank.
        // These anchors follow the dominant accent of each authored deck. Hue carries most of the
        // matching weight, while saturation/value separate neighboring variants with similar hues.
        private static readonly Color32[] MatchColors =
        {
            new Color32(195, 98, 107, 255),   // red planet
            new Color32(193, 111, 56, 255),   // orange
            new Color32(220, 213, 74, 255),   // yellow
            new Color32(147, 204, 93, 255),   // lime
            new Color32(68, 137, 108, 255),   // teal green
            new Color32(79, 180, 79, 255),    // green
            new Color32(98, 180, 197, 255),   // cyan
            new Color32(95, 108, 195, 255),   // blue
            new Color32(155, 124, 171, 255),  // purple
            new Color32(214, 135, 189, 255),  // pink
            new Color32(152, 104, 104, 255),  // brown-red
            new Color32(184, 196, 221, 255),  // pale blue/gray
            new Color32(105, 168, 63, 255),   // yellow-green
        };

        private static Sprite[] _sprites;
        private static bool _loadAttempted;

        public static bool TryGetDeckIndex(Color color, out int deckIndex)
        {
            Color.RGBToHSV(color, out float hue, out float saturation, out float value);
            float bestDistance = float.MaxValue;
            deckIndex = -1;

            for (int i = 0; i < MatchColors.Length; i++)
            {
                Color anchor = MatchColors[i];
                Color.RGBToHSV(anchor, out float anchorHue, out float anchorSaturation, out float anchorValue);

                float distance;
                if (anchorSaturation < 0.2f)
                {
                    // Hue is unstable for near-neutral colors. Keep the neutral deck's range small
                    // and based on saturation/value only.
                    float saturationDistance = Mathf.Abs(saturation - anchorSaturation);
                    float valueDistance = Mathf.Abs(value - anchorValue);
                    distance = Mathf.Sqrt(
                        saturationDistance * saturationDistance +
                        valueDistance * valueDistance);
                }
                else
                {
                    float hueDistance = Mathf.Abs(hue - anchorHue);
                    hueDistance = Mathf.Min(hueDistance, 1f - hueDistance);
                    float saturationDistance = saturation - anchorSaturation;
                    float valueDistance = value - anchorValue;
                    distance = Mathf.Sqrt(
                        Mathf.Pow(hueDistance * 3.2f, 2f) +
                        Mathf.Pow(saturationDistance * 0.12f, 2f) +
                        Mathf.Pow(valueDistance * 0.08f, 2f));
                }

                if (distance <= MatchThreshold && distance < bestDistance)
                {
                    bestDistance = distance;
                    deckIndex = i;
                }
            }

            return deckIndex >= 0;
        }

        public static Sprite GetDeckSprite(Color color)
        {
            if (!TryGetDeckIndex(color, out int deckIndex))
            {
                return null;
            }

            EnsureSpritesLoaded();
            return _sprites != null ? _sprites[deckIndex] : null;
        }

        internal static Rect GetSpriteRect(int deckIndex, int textureWidth, int textureHeight)
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
            Texture2D texture = Resources.Load<Texture2D>(ResourcePath);
            if (texture == null)
            {
                Debug.LogError($"Carrier deck sprite sheet was not found at Resources/{ResourcePath}.");
                return;
            }

            int requiredWidth = Columns * SpriteWidth;
            int requiredHeight = Rows * SpriteHeight;
            if (texture.width != requiredWidth || texture.height != requiredHeight)
            {
                Debug.LogError(
                    $"Carrier deck sprite sheet must be {requiredWidth}x{requiredHeight}, but was {texture.width}x{texture.height}.");
                return;
            }

            _sprites = new Sprite[DeckCount];
            for (int i = 0; i < DeckCount; i++)
            {
                _sprites[i] = Sprite.Create(
                    texture,
                    GetSpriteRect(i, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    PixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                _sprites[i].name = $"carrier_alts_deck_{i}";
            }
        }
    }
}
