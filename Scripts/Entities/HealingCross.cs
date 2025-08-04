using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    /// <summary>
    /// UI smybol that shows up on Beehives when they are healing a ship
    /// </summary>
    public class HealingCross : MonoBehaviour
    {
        public Beehive Beehive;
        public Transform Transform;
        private ScaledTimer _deathTimer = new ScaledTimer();
        private ScaledTimer _driftTimer = new ScaledTimer();


        public void Setup(Beehive beehive)
        {
            Beehive = beehive;

            _deathTimer.Reuse(2.5f, Kill);
            Beehive.Level.AddTimer(_deathTimer);

            _driftTimer.Reuse(0.1f, DriftUpwards, true);
            Beehive.Level.AddTimer(_driftTimer);
        }

        public void DriftUpwards()
        {
            //Debug.Log($"Drifting upwards for {Beehive.Name} healing cross at {Transform.localPosition}");
            Transform.localPosition += new Vector3(0, 0.5f, 0);
        }

        public void BeehiveKill()
        {
            Beehive.Level.CancelTimer(_deathTimer);
            Kill();
        }
        public void Kill()
        {
            Beehive.Level.CancelTimer(_driftTimer);
            Beehive.HealingCrosses.Remove(this);
            Destroy(gameObject);
        }
    }
}