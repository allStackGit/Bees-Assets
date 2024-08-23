using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipAnimationController : MonoBehaviour
    {
        public SpriteRenderer SpriteRenderer;
        public Ship Ship;
        public Sprite AnimationSpriteToRecolor, CurrentSprite;
        public Sprite[] RecoloredSprites;
        public bool ShouldSwapSprite;

        public int SpriteRows, SpriteColumns, SpriteIndex;

        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[SpriteRows * SpriteColumns];
            bool hasLoadedCachedSprites = false;
            if (Ship.FleetShip.HasCachedSprite)
            {
                hasLoadedCachedSprites = true;
                for (int i = 0; i < RecoloredSprites.Length && hasLoadedCachedSprites; i++)
                {
                    RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i, ConfigData.ShipSizes[Ship.ShipType]);
                    if (RecoloredSprites[i] == null)
                    {
                        hasLoadedCachedSprites = false;
                    }

                }
               
            }


            if (!hasLoadedCachedSprites)
            {
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(Ship.ShipType);
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, AnimationSpriteToRecolor);

                Texture2D sourceTexture = AnimationSpriteToRecolor.texture;
                Color[] pixels = sourceTexture.GetPixels();


                for (int i = 0; i < changeablePixels.Length; i++)
                {
                    pixels[changeablePixels[i]] = Ship.Squad.Color;
                }
                Texture2D changedTexture = new Texture2D(sourceTexture.width, sourceTexture.height);

                changedTexture.SetPixels(pixels);
                changedTexture.Apply(true);

                int count = 0;
                Vector2Int size = ConfigData.ShipSizes[Ship.ShipType];
                //Debug.Log($"Each sprite is {widthPerUnit} wide and {heightPerUnit} tall for a total width of {widthPerUnit * SpriteColumns} and total height of {heightPerUnit * SpriteRows}");

                for (int y = 0; y < SpriteRows; y++)
                {
                    for (int x = 0; x < SpriteColumns; x++)
                    {
                        RecoloredSprites[count] = Sprite.Create(changedTexture, new Rect(size.x * x, (sourceTexture.height - size.y * y) - size.y, size.x, size.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
                        //RecoloredSprites[count].name = $"{NamePrefix}_C_{count}";

                        bool hasCachedSprite = false;
                        try
                        {
                            Ship.FleetShip.SaveSpriteToCache(count, RecoloredSprites[count].texture.GetPixels(size.x * x, (sourceTexture.height - size.y * y) - size.y, size.x, size.y), size);
                            hasCachedSprite = true;
                        }catch(Exception e)
                        {
                            Debug.Log($"Error while trying to save cached sprites: {e}");
                        }
                        if (count == 0 && hasCachedSprite)
                        {
                            Ship.FleetShip.HasCachedSprite = true;
                            ConfigData.AllShips.SaveFleetData();
                        }
                        count++;
                    }

                }
            }
            

            //Animator.runtimeAnimatorController.animationClips[0]
        }

        void LateUpdate()
        {
            if (ShouldSwapSprite)
            {
                //Debug.Log($"Trying to swap {SpriteRenderer.sprite.name} with {RecoloredSprites[SpriteIndex % RecoloredSprites.Length].name} {FramesChange} over {timeDifference}s at {fps} fps");
                SpriteRenderer.sprite = RecoloredSprites[SpriteIndex % RecoloredSprites.Length];
                CurrentSprite = SpriteRenderer.sprite;
                SpriteIndex++;
                ShouldSwapSprite = false;

            }
            else if (Ship.Squad.HasCustomColor)
            {
                SpriteRenderer.sprite = CurrentSprite;
                //Debug.Log($"Should not swap sprite yet");
            }

        }

        public void SwapSprites()
        {
            if (Ship.Squad.HasCustomColor)
            {
                ShouldSwapSprite = true;
            }
        }
    }
}