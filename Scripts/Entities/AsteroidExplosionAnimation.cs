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
            Asteroid.Kill();
        }
    }
}