using System;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    /// <summary>
    /// Rebuilds device-local custom-color animation frames from the authored sprite currently
    /// supplied by the Animator. Cached PNGs are an optimization and are not part of cloud save
    /// data, so a squad must remain visually correct after moving to a different device.
    /// </summary>
    internal static class CustomSpriteCacheRepair
    {
        internal static Sprite RecolorAndCache(Ship ship, Sprite sourceSprite, int cacheIndex, string cacheType)
        {
            if (ship == null || ship.FleetShip == null || ship.Squad == null ||
                sourceSprite == null || !ship.Squad.HasCustomColor)
            {
                return null;
            }

            if (!ConfigData.ChangeableShipColors.TryGetValue(ship.ShipType, out Color[] changeableColors) ||
                changeableColors == null)
            {
                return null;
            }

            try
            {
                Texture2D sourceTexture = sourceSprite.texture;
                Color[] sourcePixels = sourceTexture.GetPixels();
                int[] changeablePixels = Utilities.GetChangablePixelsForImage(changeableColors, sourceSprite);
                foreach (int pixelIndex in changeablePixels)
                {
                    if (pixelIndex >= 0 && pixelIndex < sourcePixels.Length)
                    {
                        sourcePixels[pixelIndex] = ship.Squad.Color;
                    }
                }

                Rect sourceRect = sourceSprite.rect;
                int sourceX = Mathf.RoundToInt(sourceRect.x);
                int sourceY = Mathf.RoundToInt(sourceRect.y);
                int width = Mathf.RoundToInt(sourceRect.width);
                int height = Mathf.RoundToInt(sourceRect.height);
                Color[] framePixels = new Color[width * height];

                for (int y = 0; y < height; y++)
                {
                    int sourceRow = (sourceY + y) * sourceTexture.width + sourceX;
                    int targetRow = y * width;
                    Array.Copy(sourcePixels, sourceRow, framePixels, targetRow, width);
                }

                Texture2D recoloredTexture = new Texture2D(width, height);
                recoloredTexture.filterMode = FilterMode.Point;
                recoloredTexture.SetPixels(framePixels);
                recoloredTexture.Apply();

                Sprite recoloredSprite = Sprite.Create(
                    recoloredTexture,
                    new Rect(0, 0, width, height),
                    ConfigData.HalfSize,
                    ConfigData.PixelsPerUnit);

                ship.FleetShip.SaveSpriteToCache(
                    cacheIndex,
                    cacheType,
                    framePixels,
                    new Vector2Int(width, height),
                    ship.Squad.SavedSquad.Color);

                return recoloredSprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"Could not rebuild {cacheType} sprite cache for {ship.FleetShip.Name} frame {cacheIndex}: {e.Message}");
                return null;
            }
        }
    }
}
