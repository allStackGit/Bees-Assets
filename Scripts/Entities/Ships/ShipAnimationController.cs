using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    /// <summary>
    /// Controls the sprite loading and swapping for ship animations that have custom colors (e.g. the Warp Gate and Factory) See Also: RemainsAnimationController
    /// </summary>
    public class ShipAnimationController : MonoBehaviour
    {
        public SpriteRenderer SpriteRenderer;
        public Animator Animator;
        public Ship Ship;
        public Sprite CurrentSprite;
        public Sprite[] RecoloredSprites;
        public bool ShouldSwapSprite, UseSecondaryLoop, IsReadyToWarp, IsWarpGate;
        public WarpGate WarpGate;

        public int TotalSprites, SpriteIndex, ModuloIndex, SkipSprites;

        public void Setup()
        {
            if (Ship.ShipType == ConfigData.ShipTypes.WarpGate)
            {
                IsWarpGate = true;
                WarpGate = (WarpGate)Ship;
            }
        }

        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[TotalSprites];
            if (Ship.FleetShip.HasCachedSprite)
            {
                int key = (Ship.ShipType, Ship.Squad.SavedSquad.Color).GetHashCode();

                if (Ship.Stage.LoadedShipAnimationSprites.ContainsKey(key))
                {
                    RecoloredSprites = Ship.Stage.LoadedShipAnimationSprites[key];
                }
                else
                {
                    for (int i = 0; i < RecoloredSprites.Length; i++)
                    {
                        RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i + 1, "ship", ConfigData.ShipSizes[Ship.ShipType], Ship.Squad.SavedSquad.Color);
                    }

                    Ship.Stage.LoadedShipAnimationSprites[key] = RecoloredSprites;
                }
            }
        }

        private void LateUpdate()
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
                SpriteRenderer.sprite = RecoloredSprites[index];
                CurrentSprite = SpriteRenderer.sprite;
                SpriteIndex++;
                ShouldSwapSprite = false;
            }
            else if (Ship.Squad.HasCustomColor)
            {
                SpriteRenderer.sprite = CurrentSprite;
            }
        }

        public void SwapSprites()
        {
            if (Ship.Squad.HasCustomColor)
            {
                ShouldSwapSprite = true;
            }
        }

        public void Activate()
        {
            if (!Ship.Stage.IsTraining || IsWarpGate)
            {
                // Each activation is a new animation session. A destroyed pooled Warp Gate can
                // skip FullRetreat cleanup, so command-significant readiness and frame state must
                // be reset here rather than depending on the previous command to finalize cleanly.
                ShouldSwapSprite = false;
                UseSecondaryLoop = false;
                IsReadyToWarp = false;
                SpriteIndex = 0;

                SpriteRenderer.enabled = true;
                Animator.enabled = true;
                Animator.Rebind();
                Animator.Update(0f);

                if (IsWarpGate)
                {
                    Animator.Play("Warp Gate Opening", 0, 0f);
                    Animator.Update(0f);
                    if (Ship.Stage.ActivateAudio)
                    {
                        WarpGate.WarpGateStartingSound.enabled = true;
                        WarpGate.WarpGateLoopingSound.enabled = true;
                        WarpGate.WarpGateStartingSound.Play();
                    }
                }

                CurrentSprite = SpriteRenderer.sprite;
                enabled = true;
            }
        }

        public void Deactivate()
        {
            if (!Ship.Stage.IsTraining || IsWarpGate)
            {
                SpriteRenderer.enabled = false;
                Animator.enabled = false;
                enabled = false;

                if (IsWarpGate && WarpGate.IsAudioLoaded)
                {
                    WarpGate.WarpGateStartingSound.enabled = false;
                    WarpGate.WarpGateLoopingSound.enabled = false;
                }
            }
        }

        /// <summary>
        /// This changes the animation by making it skip a certain number of sprites and loop a different number of sprites, effectively creating a new loop from a subset of sprites
        /// </summary>
        public void ChangeSpriteLoop()
        {
            Debug.Log($"{Ship.Name} Changing sprite loop, ready to warp");
            UseSecondaryLoop = true;
            IsReadyToWarp = true;
            if (WarpGate.IsAudioLoaded)
            {
                WarpGate.WarpGateStartingSound.Stop();
                WarpGate.WarpGateLoopingSound.Play();
            }
        }
    }
}
