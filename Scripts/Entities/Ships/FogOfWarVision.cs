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
        private Level _fadeLevel;

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
            Vector2 shipPosition = Ship.GetPosition();
            Vector3 currentPosition = Transform.position;
            if (currentPosition.x != shipPosition.x || currentPosition.y != shipPosition.y || currentPosition.z != 0f)
            {
                // SpriteMask transforms can participate in renderer/culling updates. Most ships are
                // stationary on many frames, so avoid dirtying the transform and mask hierarchy when
                // the vision object is already exactly where it belongs.
                Transform.position = shipPosition;
            }
        }

        public void Activate()
        {
            if (_ownerLevel != null)
            {
                _ownerLevel.CancelTimer(_shrinkVisionStartTimer);
                _ownerLevel.CancelTimer(_shrinkVisionTimer);
                _ownerLevel.State.FogOfWarVisions.Remove(this);
            }

            _fadeLevel = null;
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
            _fadeLevel = null;
            enabled = false;
            FogIlluminator.enabled = false;
        }

        public void Kill(float initialDelay, bool endKill)
        {
            if (!endKill)
            {
                Transform.position = Ship.GetPosition();
                enabled = false;
                _fadeLevel = _ownerLevel;
                if (_fadeLevel == null)
                {
                    Deactivate();
                    return;
                }

                _shrinkVisionTimer.Reuse(.1f, ShrinkVision, true);
                _shrinkVisionStartTimer.Reuse(initialDelay, StartShrinkVision);
                _fadeLevel.AddTimer(_shrinkVisionStartTimer);
            }
            else
            {
                Deactivate();
            }
        }

        private void StartShrinkVision()
        {
            Level fadeLevel = _fadeLevel;
            if (fadeLevel != null && _ownerLevel == fadeLevel)
            {
                fadeLevel.AddTimer(_shrinkVisionTimer);
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
