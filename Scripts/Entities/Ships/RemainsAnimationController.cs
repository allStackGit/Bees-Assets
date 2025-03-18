using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    /// <summary>
    /// Controls the sprite loading and swapping for ship remains animations that have custom colors. See also: ShipAnimationController
    /// </summary>
    public class RemainsAnimationController : MonoBehaviour
    {

        public SpriteRenderer SpriteRenderer;
        public Ship Ship;
        public Sprite CurrentSprite;
        public Sprite[] RecoloredSprites;
        public bool ShouldSwapSprite;

        public int TotalSprites, SpriteIndex;

        private int _loopIndex;
        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[TotalSprites];
            if (Ship.FleetShip.HasCachedSprite)
            {
                for (_loopIndex = 0; _loopIndex < RecoloredSprites.Length; _loopIndex++)
                {
                    RecoloredSprites[_loopIndex] = Ship.FleetShip.LoadCachedSprite(_loopIndex, "remains", ConfigData.ShipRemainsSizes[Ship.ShipType], Ship.Squad.SavedSquad.Color); 
                }
                Debug.Log($"Loaded cached sprites for Ship {Ship.Name}");
            }
        }

        private int _index;
        void LateUpdate()
        {
            if (ShouldSwapSprite)
            {
                _index = SpriteIndex % RecoloredSprites.Length;
                
                Debug.Log($"Recolored index: {_index}");
                Debug.Log($"Trying to swap {SpriteRenderer.sprite.name} with {RecoloredSprites[_index].name}");
                SpriteRenderer.sprite = RecoloredSprites[_index];
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

        public void Kill()
        {
            gameObject.SetActive(false);
        }
    }
}