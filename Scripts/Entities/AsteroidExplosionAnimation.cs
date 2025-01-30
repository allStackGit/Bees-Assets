using Assets.Scripts.Entities.Projectiles;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class AsteroidExplosionAnimation : ExplosionAnimation
    {
        public CollisionAsteroid Asteroid;
        public void KillAsteroid()
        {
            Debug.LogError($"Collsion asteroid explosion animation called");
            Asteroid.Kill();
        }
    }
}