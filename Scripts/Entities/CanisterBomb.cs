using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    class CanisterBomb : MapObject
    {
        private const int DamageStageCount = 4;
        private int _spriteVariantStartIndex;

        public RocketExplosion Explosion;
        public int Power;
        public Obstacle TargetObstacle;

        protected override void InitializeSprite()
        {
            if (SpriteRenderer == null || Sprites == null || Sprites.Length < DamageStageCount)
            {
                base.InitializeSprite();
                return;
            }

            int variantCount = Sprites.Length / DamageStageCount;
            _spriteVariantStartIndex = Utilities.RandomInt(variantCount) * DamageStageCount;
            SpriteRenderer.sprite = Sprites[_spriteVariantStartIndex];
        }

        protected override void OnHealthChanged()
        {
            if (SpriteRenderer == null || Sprites == null || MaxHealth <= 0 ||
                _spriteVariantStartIndex + DamageStageCount > Sprites.Length)
            {
                return;
            }

            int lostHealth = MaxHealth - Mathf.Max(Health, 0);
            int damageStage = Mathf.Clamp((lostHealth * DamageStageCount) / MaxHealth, 0, DamageStageCount - 1);
            SpriteRenderer.sprite = Sprites[_spriteVariantStartIndex + damageStage];
        }

        public override void Kill()
        {
            if (!IsDead)
            {
                Debug.Log("Canister bomb killed");
                Explosion = (RocketExplosion)Level.Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.FireTankExplosion);
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.Setup(Level, LastHitProjectile.Weapon, LastHitProjectile.Shooter, null, transform.localPosition, 0, 0, Power);
                IsDead = true;
                Destroy(gameObject);
                TargetObstacle.Kill(); 
            }
        }
    }
}
