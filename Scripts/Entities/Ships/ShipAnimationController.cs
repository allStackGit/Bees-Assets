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
        public bool ShouldSwapSprite;

        public int TotalSprites, SpriteIndex;

        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[TotalSprites];
            if (Ship.FleetShip.HasCachedSprite)
            {
                for (int i = 0; i < RecoloredSprites.Length; i++)
                {
                    RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i+1, ConfigData.ShipSizes[Ship.ShipType]); // skips the first sprite because that's the base sprite
                }
            }
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