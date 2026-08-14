using Assets.Scripts.Levels.Commands;
using System;
using System.Collections.Generic;
using System.IO;
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
            int key = (Ship.ShipType, Ship.Squad.SavedSquad.Color).GetHashCode();

            if (Ship.Stage.LoadedShipAnimationSprites.ContainsKey(key))
            {
                RecoloredSprites = Ship.Stage.LoadedShipAnimationSprites[key];
                return;
            }

            // The HasCachedSprite bit is persisted with the fleet, while these PNGs live only on
            // the current device. Probe disk directly so transferred saves can discover whatever
            // local frames exist and lazily rebuild the missing ones during animation playback.
            for (int i = 0; i < RecoloredSprites.Length; i++)
            {
                RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(
                    i + 1,
                    "ship",
                    ConfigData.ShipSizes[Ship.ShipType],
                    Ship.Squad.SavedSquad.Color); // skips the first sprite because that's the base sprite
            }

            Ship.Stage.LoadedShipAnimationSprites[key] = RecoloredSprites;
        }
        //void Update()
        //{
        //    if (Ship.IsWarpGate && ((WarpGate)Ship).ShipsWarpingHere.Count == 0)
        //    {
        //        Debug.LogWarning($"Warp gate {Ship.Name} has no ships warping here, but the animation is not disabled");
        //    }
        //    else if (Ship.ShipType == ConfigData.ShipTypes.Factory && !(Ship.Squad.HasCommand && Ship.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Mining &&
        //        ((Mining)Ship.Squad.GetCommand()).HasFoundAsteroid))
        //    {
        //        Debug.LogWarning($"Factory {Ship.Name} has no ships mining, but the animation is not disabled");
        //    }
        //}

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

                Sprite recoloredSprite = RecoloredSprites[index];
                if (recoloredSprite == null)
                {
                    // Animator has already supplied the authored frame for this animation event.
                    // Recolor that frame in-place, cache it locally, and retain it in the Stage
                    // cache so subsequent ships with the same squad color pay no regeneration cost.
                    recoloredSprite = CustomSpriteCacheRepair.RecolorAndCache(
                        Ship,
                        SpriteRenderer.sprite,
                        index + 1,
                        "ship");
                    if (recoloredSprite != null)
                    {
                        RecoloredSprites[index] = recoloredSprite;
                    }
                }

                if (recoloredSprite != null)
                {
                    SpriteRenderer.sprite = recoloredSprite;
                    CurrentSprite = recoloredSprite;
                }
                else
                {
                    // Never replace a valid authored animation frame with null just because a
                    // device-local cache entry is unavailable.
                    CurrentSprite = SpriteRenderer.sprite;
                }

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
        public void Activate()
        {
            if (!Ship.Stage.IsTraining || IsWarpGate)
            {
                // Every activation is a fresh animation session. A destroyed pooled Warp Gate can
                // bypass FullRetreat cleanup, so readiness/frame state cannot belong to the prior use.
                ShouldSwapSprite = false;
                UseSecondaryLoop = false;
                IsReadyToWarp = false;
                SpriteIndex = 0;

                SpriteRenderer.enabled = true;
                Animator.enabled = true;
                Animator.Rebind();
                Animator.Update(0f); // reset the animation to the first frame
                enabled = true;

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
        /// <param name="moduloIndex"></param>
        /// <param name="skipSprites"></param>
        public void ChangeSpriteLoop()
        {
            Debug.Log($"{Ship.Name} Changing sprite loop, ready to warp");
            UseSecondaryLoop = true;
            IsReadyToWarp = true; // this is called by the warp gate animation which makes the animation necessary for non-visual reasons
            if (WarpGate.IsAudioLoaded)
            {
                WarpGate.WarpGateStartingSound.Stop();
                WarpGate.WarpGateLoopingSound.Play();
            }

        }
    }
}
