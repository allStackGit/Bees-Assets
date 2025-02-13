using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class RemainsAnimationController : MonoBehaviour
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
                    RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i, "remains", ConfigData.ShipSizes[Ship.ShipType], Ship.Squad.SavedSquad.Color); 
                }
                //Debug.Log($"Loaded cached sprites for Ship {Ship.Name}");
            }
        }

        void LateUpdate()
        {
            if (ShouldSwapSprite)
            {
                int index = SpriteIndex % RecoloredSprites.Length;
                
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
    }
}