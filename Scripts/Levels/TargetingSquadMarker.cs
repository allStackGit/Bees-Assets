using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class TargetingSquadMarker : MonoBehaviour
    {
        public int Loops = 0;
        public ScaledTimer killTimer = new ScaledTimer();
        public Ship EnemyShip;
        public Level Level;
        public float Frequency = .25f;
        public int MaxLoops;

        public void Setup(Ship enemyShip)
        {
            EnemyShip = enemyShip;
            Level = EnemyShip.Level;
            EnemyShip.Level.State.TargetingSquadMarkers.Add(this);
            MaxLoops = (int)(2f / Frequency);
            killTimer.Reuse(Frequency, CheckKill, true);
            Level.AddTimer(killTimer);
        }

        public void CheckKill()
        {
            if (EnemyShip == null || EnemyShip.IsDead || Level == null || Level.State == null ||
                Level.State.LevelEnded || Level.State.GameOver || !Level.IsLevelConnectedToServer ||
                Loops >= MaxLoops)
            {
                Kill();
            }
            else
            {
                Loops++;
            }
        }

        public void Kill()
        {
            // Destroy is deferred until the end of the frame. Disable the marker immediately so
            // its animation cannot flash over a darkened/final-dialogue campaign screen while the
            // old Level is being torn down.
            gameObject.SetActive(false);

            if (Level != null)
            {
                Level.CancelTimer(killTimer);
                Level.State?.TargetingSquadMarkers.Remove(this);
            }
            Destroy(gameObject);
        }
    }
}
