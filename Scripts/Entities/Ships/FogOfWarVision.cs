using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class FogOfWarVision : MonoBehaviour
    {
        public int Range;
        public Ship Ship;
        public SpriteMask FogIlluminator;
        public Transform Transform;

        public void Create(Ship ship)
        {
            Ship = ship;
            Range = Ship.Sight * 2;
            if (Range == 0)
            {
                Range = Ship.MaxRange * 2;
            }
            Transform.SetParent(Ship.transform);
            Transform.localScale = new Vector3(Range, Range, 0);
        }
        public void Activate()
        {

            enabled = true;
            FogIlluminator.enabled = true;
        }
        public void Deactivate()
        {
            enabled = false;
            FogIlluminator.enabled = false;
        }

        private ScaledTimer _shrinkVisionStartTimer = new ScaledTimer();
        private ScaledTimer _shrinkVisionTimer = new ScaledTimer();
        public void Kill(float initialDelay)
        {
            Transform.SetParent(Ship.Level.Map.Transform);
            _shrinkVisionTimer.Reuse(.1f, ShrinkVision, true);
            _shrinkVisionStartTimer.Reuse(initialDelay, () =>
            {
                Ship.Level.AddTimer(_shrinkVisionTimer);
            });
            Ship.Level.AddTimer(_shrinkVisionStartTimer);
            //InvokeRepeating(nameof(ShrinkVision), initialDelay, .1f);
        }

        public void ShrinkVision()
        {
            Transform.localScale *= ConfigData.VisionShrinkingMultiplier;
            if (Transform.localScale.x < 3)
            {
                Ship.Level.CancelTimer(_shrinkVisionTimer);
                //CancelInvoke(nameof(ShrinkVision));
                Deactivate();
            }
        }
    }
}