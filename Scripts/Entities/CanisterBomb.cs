using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    class CanisterBomb : MapObject
    {
        public RocketExplosion Explosion;
        public int Power;
        public Obstacle TargetObstacle;

        public override void Kill()
        {
            if (!IsDead)
            {
                Debug.Log("Canister bomb killed");
                Explosion = (RocketExplosion)Level.Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.RocketExplosion);
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.Setup(Level, LastHitProjectile.Weapon, LastHitProjectile.Shooter, null, transform.localPosition, 0, 0, Power);
                IsDead = true;
                Destroy(gameObject);
                TargetObstacle.Kill(); 
            }
        }
    }
}
