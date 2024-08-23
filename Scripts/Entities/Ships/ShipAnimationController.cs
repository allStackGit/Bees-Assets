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
            int widthPerUnit = sourceTexture.width / SpriteColumns;
            int heightPerUnit = sourceTexture.height / SpriteRows;

            //Debug.Log($"Each sprite is {widthPerUnit} wide and {heightPerUnit} tall for a total width of {widthPerUnit * SpriteColumns} and total height of {heightPerUnit * SpriteRows}");

            for (int y = 0; y < SpriteRows; y++)
            {
                for (int x = 0; x < SpriteColumns; x++)
                {
                    RecoloredSprites[count] = Sprite.Create(changedTexture, new Rect(widthPerUnit * x, (sourceTexture.height - heightPerUnit * y) - heightPerUnit, widthPerUnit, heightPerUnit), Vector2.one / 2, ConfigData.PixelsPerUnit);
                    //RecoloredSprites[count].name = $"{NamePrefix}_C_{count}";

                    //string path = $"{ConfigData.GetBasePath()}/debug/{RecoloredSprites[count].name}.png";
                    //Texture2D export = new Texture2D(widthPerUnit, heightPerUnit);
                    //export.SetPixels(RecoloredSprites[count].texture.GetPixels(widthPerUnit * x, (sourceTexture.height - heightPerUnit * y) - heightPerUnit, widthPerUnit, heightPerUnit));
                    //export.Apply();
                    //File.WriteAllBytes(path, export.EncodeToPNG());
                    count++;
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