using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class TargetingSquadMarker : MonoBehaviour
    {
        public int Loops;
        public Ship EnemyShip;
        public Level Level;
        public float Frequency = .25f;
        public int MaxLoops;

        private TargetingSquadMarkerPool _pool;
        private float _elapsed;
        private bool _isActive;

        public void Setup(TargetingSquadMarkerPool pool, Ship enemyShip)
        {
            _pool = pool;
            EnemyShip = enemyShip;
            Level = EnemyShip.Level;
            Loops = 0;
            _elapsed = 0f;
            _isActive = true;
            MaxLoops = Frequency > 0f ? (int)(2f / Frequency) : 0;
            EnemyShip.Level.State.TargetingSquadMarkers.Add(this);
        }

        public void Tick(float deltaTime)
        {
            if (!_isActive)
            {
                return;
            }

            if (EnemyShip == null || EnemyShip.IsDead)
            {
                Kill();
                return;
            }

            if (Frequency <= 0f)
            {
                Kill();
                return;
            }

            _elapsed += deltaTime;
            if (_elapsed > Frequency)
            {
                _elapsed -= Frequency;
                CheckKill();
            }
        }

        public void CheckKill()
        {
            if (!_isActive)
            {
                return;
            }

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
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            if (Level?.State != null)
            {
                Level.State.TargetingSquadMarkers.Remove(this);
            }

            if (_pool != null)
            {
                _pool.Release(this);
            }
            else
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
            }
        }

        public void ResetForPool()
        {
            _isActive = false;
            _elapsed = 0f;
            Loops = 0;
            MaxLoops = 0;
            EnemyShip = null;
            Level = null;
            _pool = null;
        }
    }
}