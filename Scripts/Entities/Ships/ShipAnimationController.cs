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
        public bool ShouldSwapSprite, UseSecondaryLoop, IsReadyToWarp;

        public int TotalSprites, SpriteIndex, ModuloIndex, SkipSprites;

        public void RecolorAnimationSprites()
        {
            RecoloredSprites = new Sprite[TotalSprites];
            if (Ship.FleetShip.HasCachedSprite)
            {

                int key = (Ship.ShipType, Ship.Squad.SavedSquad.Color).GetHashCode();

                if (Ship.Stage.LoadedShipAnimationSprites.ContainsKey(key))
                {
                    RecoloredSprites = Ship.Stage.LoadedShipAnimationSprites[key];
                    Debug.Log($"Loaded cached sprites from Stage instead of Disk for {Ship.ShipType} with {Ship.Squad.SavedSquad.Color}");
                }
                else
                {
                    for (int i = 0; i < RecoloredSprites.Length; i++)
                    {
                        RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(i + 1, "ship", ConfigData.ShipSizes[Ship.ShipType], Ship.Squad.SavedSquad.Color); // skips the first sprite because that's the base sprite
                    }

                    Ship.Stage.LoadedShipAnimationSprites[key] = RecoloredSprites;
                }


                //Debug.Log($"Loaded cached sprites for Ship {Ship.Name}");


            }
        }
        void Update()
        {
            if (Ship.IsWarpGate && ((WarpGate)Ship).ShipsWarpingHere.Count == 0)
            {
                Debug.LogWarning($"Warp gate {Ship.Name} has no ships warping here, but the animation is not disabled");
            }
            else if (Ship.ShipType == ConfigData.ShipTypes.Factory && !(Ship.Squad.HasCommand && Ship.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Mining &&
                ((Mining)Ship.Squad.GetCommand()).HasFoundAsteroid))
            {
                Debug.LogWarning($"Factory {Ship.Name} has no ships mining, but the animation is not disabled");
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
        public void Activate()
        {
            if (Ship.Stage.IsRendering || Ship.ShipType == ConfigData.ShipTypes.WarpGate)
            {
                SpriteRenderer.enabled = true;
                Animator.enabled = true;
                enabled = true;

                if (Ship.ShipType == ConfigData.ShipTypes.WarpGate)
                {
                    Animator.Play("Warp Gate Opening", 0, 0f);
                }
            }

        }
        public void Deactivate()
        {
            if (Ship.Stage.IsRendering || Ship.ShipType == ConfigData.ShipTypes.WarpGate)
            {
                SpriteRenderer.enabled = false;
                Animator.enabled = false;
                enabled = false;
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
        }
    }
}