using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class FogOfWarVision : MonoBehaviour
    {
        public int Range;
        public Ship Ship;
        public SpriteMask FogIlluminator;
        public Transform Transform;

        private readonly ScaledTimer _shrinkVisionStartTimer = new ScaledTimer();
        private readonly ScaledTimer _shrinkVisionTimer = new ScaledTimer();
        private Level _ownerLevel;

        public void Create(Ship ship)
        {
            Ship = ship;
            Range = Ship.Sight * 2;
            if (Range == 0)
            {
                Range = Ship.MaxRange * 2;
            }
            Transform.SetParent(Ship.transform.parent);
        }

        public void LateUpdate()
        {
            Transform.position = Ship.GetPosition();
        }

        public void Activate()
        {
            // A pooled Ship can be reused on another Level while its old death vision is
            // still fading. Tear down the previous lifecycle through the Level that actually
            // owns those timers/registries before adopting the Ship's new Level.
            if (_ownerLevel != null)
            {
                _ownerLevel.CancelTimer(_shrinkVisionStartTimer);
                _ownerLevel.CancelTimer(_shrinkVisionTimer);
                _ownerLevel.State.FogOfWarVisions.Remove(this);
            }

            _ownerLevel = Ship.Level;
            if (!_ownerLevel.State.FogOfWarVisions.Contains(this))
            {
                _ownerLevel.State.FogOfWarVisions.Add(this);
            }
            Transform.position = Ship.GetPosition();
            Transform.localScale = new Vector3(Range, Range, 0);
            enabled = true;
            FogIlluminator.enabled = true;
        }

        public void Deactivate()
        {
            if (_ownerLevel != null)
            {
                _ownerLevel.CancelTimer(_shrinkVisionStartTimer);
                _ownerLevel.CancelTimer(_shrinkVisionTimer);
                _ownerLevel.State.FogOfWarVisions.Remove(this);
            }
            enabled = false;
            FogIlluminator.enabled = false;
        }

        public void Kill(float initialDelay, bool endKill)
        {
            if (!endKill)
            {
                // Freeze the death vision at the ship's final position. Ship is pooled
                // independently and may be reused while this visual fade is still alive.
                Transform.position = Ship.GetPosition();
                enabled = false;
                Level fadeLevel = _ownerLevel;
                if (fadeLevel == null)
                {
                    Deactivate();
                    return;
                }

                _shrinkVisionTimer.Reuse(.1f, ShrinkVision, true);
                _shrinkVisionStartTimer.Reuse(initialDelay, () =>
                {
                    if (_ownerLevel == fadeLevel)
                    {
                        fadeLevel.AddTimer(_shrinkVisionTimer);
                    }
                });
                fadeLevel.AddTimer(_shrinkVisionStartTimer);
            }
            else
            {
                Deactivate();
            }
        }

        public void ShrinkVision()
        {
            Transform.localScale *= ConfigData.VisionShrinkingMultiplier;
            if (Transform.localScale.x < 3)
            {
                _ownerLevel?.CancelTimer(_shrinkVisionTimer);
                Deactivate();
            }
        }
    }
}
