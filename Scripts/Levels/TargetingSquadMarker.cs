using Assets.Scripts.Entities.Ships;
using System.Collections;
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
            MaxLoops = (int) (2f / Frequency);
            killTimer.Reuse(Frequency, CheckKill, true);
            Level.AddTimer(killTimer);
        }

        public void CheckKill()
        {
            if (EnemyShip.IsDead || Loops >= MaxLoops)
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
            Level.CancelTimer(killTimer);
            Level.State.TargetingSquadMarkers.Remove(this);
            Destroy(gameObject);
        }
    }
}