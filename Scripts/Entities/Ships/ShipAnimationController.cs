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
        public Sprite CurrentSprite;
        public Sprite[] RecoloredSprites;
        public bool ShouldSwapSprite, UseSecondaryLoop, IsReadyToWarp;

        public int TotalSprites, SpriteIndex, ModuloIndex, SkipSprites;

        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[TotalSprites];
            if (Ship.FleetShip.HasCachedSprite)
            {
                for (int i = 0; i < RecoloredSprites.Length; i++)
                {
                    RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i+1, "ship", ConfigData.ShipSizes[Ship.ShipType], Ship.Squad.SavedSquad.Color); // skips the first sprite because that's the base sprite
                }
            }
        }

        void LateUpdate()
        {
            if (ShouldSwapSprite)
            {
                int index;
                if (UseSecondaryLoop)
                {
                    index = (SpriteIndex % ModuloIndex) + SkipSprites;
                }
                else
                {
                    index = SpriteIndex % RecoloredSprites.Length;
                }
                //Debug.Log($"Recolored index: {index}");
                //Debug.Log($"Trying to swap {SpriteRenderer.sprite.name} with {RecoloredSprites[SpriteIndex % RecoloredSprites.Length].name} {FramesChange} over {timeDifference}s at {fps} fps");
                SpriteRenderer.sprite = RecoloredSprites[index];
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

        /// <summary>
        /// This changes the animation by making it skip a certain number of sprites and loop a different number of sprites, effectively creating a new loop from a subset of sprites
        /// </summary>
        /// <param name="moduloIndex"></param>
        /// <param name="skipSprites"></param>
        public void ChangeSpriteLoop()
        {
            UseSecondaryLoop = true;
            IsReadyToWarp = true;
        }
    }
}