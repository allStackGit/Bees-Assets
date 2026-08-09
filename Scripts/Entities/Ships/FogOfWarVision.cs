using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class FogOfWarVision : MonoBehaviour
    {
        public int Range;
        public Ship Ship;
        public SpriteMask FogIlluminator;
        public Transform Transform;

        private ScaledTimer _shrinkVisionStartTimer = new ScaledTimer();
        private ScaledTimer _shrinkVisionTimer = new ScaledTimer();

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
            // This object belongs to a pooled Ship. A death-fade from the previous
            // use must not shrink or reposition the newly activated vision.
            Ship.Level.CancelTimer(_shrinkVisionStartTimer);
            Ship.Level.CancelTimer(_shrinkVisionTimer);
            if (!Ship.Level.State.FogOfWarVisions.Contains(this))
            {
                Ship.Level.State.FogOfWarVisions.Add(this);
            }
            Transform.position = Ship.GetPosition();
            Transform.localScale = new Vector3(Range, Range, 0);
            enabled = true;
            FogIlluminator.enabled = true;
        }

        public void Deactivate()
        {
            Ship.Level.CancelTimer(_shrinkVisionStartTimer);
            Ship.Level.CancelTimer(_shrinkVisionTimer);
            Ship.Level.State.FogOfWarVisions.Remove(this);
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
                _shrinkVisionTimer.Reuse(.1f, ShrinkVision, true);
                _shrinkVisionStartTimer.Reuse(initialDelay, () =>
                {
                    Ship.Level.AddTimer(_shrinkVisionTimer);
                });
                Ship.Level.AddTimer(_shrinkVisionStartTimer);
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
                Ship.Level.CancelTimer(_shrinkVisionTimer);
                Deactivate();
            }
        }
    }
}
