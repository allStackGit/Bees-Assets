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

        public void ResetForReuse()
        {
            ShouldSwapSprite = false;
            SpriteIndex = 0;
            CurrentSprite = null;
        }

        private int _loopIndex;
        public void RecolorAnimationSprites()
        {
            int key = (Ship.ShipType, Ship.Squad.SavedSquad.Color).GetHashCode();
            if (Ship.Stage.LoadedRemainsSprites.TryGetValue(key, out Sprite[] cachedSprites))
            {
                RecoloredSprites = cachedSprites;
                return;
            }

            RecoloredSprites = new Sprite[TotalSprites];
            // Custom-color cache files are device-local. Load every frame that exists and leave
            // missing entries null so LateUpdate can rebuild them from the authored animation.
            for (_loopIndex = 0; _loopIndex < RecoloredSprites.Length; _loopIndex++)
            {
                RecoloredSprites[_loopIndex] = Ship.FleetShip.LoadCachedSprite(
                    _loopIndex,
                    "remains",
                    ConfigData.ShipRemainsSizes[Ship.ShipType],
                    Ship.Squad.SavedSquad.Color);
            }

            Ship.Stage.LoadedRemainsSprites[key] = RecoloredSprites;
        }

        private int _index;
        void LateUpdate()
        {
            if (ShouldSwapSprite)
            {
                _index = SpriteIndex % RecoloredSprites.Length;
                Sprite recoloredSprite = RecoloredSprites[_index];
                if (recoloredSprite == null)
                {
                    recoloredSprite = CustomSpriteCacheRepair.RecolorAndCache(
                        Ship,
                        SpriteRenderer.sprite,
                        _index,
                        "remains");
                    if (recoloredSprite != null)
                    {
                        RecoloredSprites[_index] = recoloredSprite;
                    }
                }

                if (recoloredSprite != null)
                {
                    SpriteRenderer.sprite = recoloredSprite;
                    CurrentSprite = recoloredSprite;
                }
                else
                {
                    CurrentSprite = SpriteRenderer.sprite;
                }

                SpriteIndex++;
                ShouldSwapSprite = false;

            }
            else if (Ship.Squad.HasCustomColor && CurrentSprite != null && SpriteRenderer.sprite != CurrentSprite)
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
