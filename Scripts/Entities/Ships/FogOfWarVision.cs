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
            Transform.SetParent(Ship.transform.parent);
        }
        public void LateUpdate()
        {
            Transform.position = Ship.GetPosition();
        }
        public void Activate()
        {
            Debug.Log($"Activating fog of war vision for {Ship.Name} with range {Range}");
            Ship.Level.State.FogOfWarVisions.Add(this);
            //Transform.SetParent(Ship.transform.parent);
            Transform.localScale = new Vector3(Range, Range, 0);
            enabled = true;
            FogIlluminator.enabled = true;
        }
        public void Deactivate()
        {
            Debug.Log($"Deactivating fog of war vision for {Ship.Name} with range {Range}");
            Ship.Level.CancelTimer(_shrinkVisionStartTimer);
            Ship.Level.CancelTimer(_shrinkVisionTimer);
            Ship.Level.State.FogOfWarVisions.Remove(this);
            enabled = false;
            FogIlluminator.enabled = false;
        }

        private ScaledTimer _shrinkVisionStartTimer = new ScaledTimer();
        private ScaledTimer _shrinkVisionTimer = new ScaledTimer();
        public void Kill(float initialDelay, bool endKill)
        {
            Debug.Log($"Killing fog of war vision for {Ship.Name}");
            //Transform.SetParent(Ship.Level.Map.Transform);
            if (!endKill)
            {
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