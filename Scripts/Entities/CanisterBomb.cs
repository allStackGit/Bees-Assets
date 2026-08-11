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
        private int _damageStage;
        private SpriteRenderer[] _smokeRenderers;
        private float _smokeFrameTimer;
        private int _smokeFrame;

        public RocketExplosion Explosion;
        public int Power;
        public Obstacle TargetObstacle;
        public Sprite[] SmokeSprites;
        public Vector2[] SmokePositions;
        public float SmokeFramesPerSecond = 5f;
        public Sprite[] ObstacleDebrisSprites;
        public int ObstacleDebrisCount = 24;
        public float ObstacleDebrisMinSpeed = 4f;
        public float ObstacleDebrisMaxSpeed = 11f;
        public float ObstacleDebrisMaxSpin = 360f;
        public float ObstacleDebrisLifetime = 1.5f;
        public float ObstacleDebrisDamping = 1.25f;
        public float ObstacleDebrisMinScale = 3f;
        public float ObstacleDebrisMaxScale = 6f;

        protected override void InitializeSprite()
        {
            if (SpriteRenderer == null || Sprites == null || Sprites.Length < DamageStageCount)
            {
                base.InitializeSprite();
                return;
            }

            int variantCount = Sprites.Length / DamageStageCount;
            _spriteVariantStartIndex = Utilities.RandomInt(variantCount) * DamageStageCount;
            _damageStage = 0;
            SpriteRenderer.sprite = Sprites[_spriteVariantStartIndex];
            InitializeSmokePlumes();
        }

        private void InitializeSmokePlumes()
        {
            int plumeCount = DamageStageCount - 1;
            _smokeRenderers = new SpriteRenderer[plumeCount];
            _smokeFrameTimer = 0f;
            _smokeFrame = 0;

            for (int i = 0; i < plumeCount; i++)
            {
                GameObject smokeObject = new GameObject($"Damage Smoke {i + 1}");
                smokeObject.transform.SetParent(transform, false);
                if (SmokePositions != null && i < SmokePositions.Length)
                {
                    smokeObject.transform.localPosition = SmokePositions[i];
                }

                SpriteRenderer smokeRenderer = smokeObject.AddComponent<SpriteRenderer>();
                smokeRenderer.sortingLayerID = SpriteRenderer.sortingLayerID;
                smokeRenderer.sortingOrder = SpriteRenderer.sortingOrder + 1;
                smokeRenderer.enabled = false;
                if (SmokeSprites != null && SmokeSprites.Length > 0)
                {
                    smokeRenderer.sprite = SmokeSprites[i % SmokeSprites.Length];
                }

                _smokeRenderers[i] = smokeRenderer;
            }
        }

        protected override void OnHealthChanged()
        {
            if (SpriteRenderer == null || Sprites == null || MaxHealth <= 0 ||
                _spriteVariantStartIndex + DamageStageCount > Sprites.Length)
            {
                return;
            }

            int lostHealth = MaxHealth - Mathf.Max(Health, 0);
            _damageStage = Mathf.Clamp((lostHealth * DamageStageCount) / MaxHealth, 0, DamageStageCount - 1);
            SpriteRenderer.sprite = Sprites[_spriteVariantStartIndex + _damageStage];
            UpdateSmokeVisibility();
        }

        private void UpdateSmokeVisibility()
        {
            if (_smokeRenderers == null)
            {
                return;
            }

            for (int i = 0; i < _smokeRenderers.Length; i++)
            {
                if (_smokeRenderers[i] != null)
                {
                    _smokeRenderers[i].enabled = i < _damageStage;
                }
            }
        }

        private void Update()
        {
            if (_damageStage <= 0 || _smokeRenderers == null || SmokeSprites == null ||
                SmokeSprites.Length == 0 || SmokeFramesPerSecond <= 0f)
            {
                return;
            }

            _smokeFrameTimer += Time.deltaTime;
            float frameDuration = 1f / SmokeFramesPerSecond;
            if (_smokeFrameTimer < frameDuration)
            {
                return;
            }

            int elapsedFrames = Mathf.FloorToInt(_smokeFrameTimer / frameDuration);
            _smokeFrameTimer -= elapsedFrames * frameDuration;
            _smokeFrame = (_smokeFrame + elapsedFrames) % SmokeSprites.Length;

            for (int i = 0; i < _smokeRenderers.Length; i++)
            {
                if (_smokeRenderers[i] != null && _smokeRenderers[i].enabled)
                {
                    _smokeRenderers[i].sprite = SmokeSprites[(_smokeFrame + i) % SmokeSprites.Length];
                }
            }
        }

        public override void Kill()
        {
            if (!IsDead)
            {
                Debug.Log("Canister bomb killed");
                Explosion = (RocketExplosion)Level.Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.FireTankExplosion);
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.Setup(Level, LastHitProjectile.Weapon, LastHitProjectile.Shooter, null, transform.localPosition, 0, 0, Power);
                Explosion.InheritCommandAttributionFrom(LastHitProjectile);
                if (Explosion.Shooter != null)
                {
                    Explosion.Shooter.ProjectilesInFlight.Add(Explosion);
                }

                AudioSource explosionAudio = Explosion != null ? Explosion.GetComponent<AudioSource>() : null;
                if (explosionAudio != null && explosionAudio.clip != null)
                {
                    explosionAudio.Stop();
                    explosionAudio.Play();
                }

                IsDead = true;

                if (TargetObstacle != null)
                {
                    TargetObstacle.BreakApart(transform.position, ObstacleDebrisSprites,
                        ObstacleDebrisCount, ObstacleDebrisMinSpeed, ObstacleDebrisMaxSpeed,
                        ObstacleDebrisMaxSpin, ObstacleDebrisLifetime, ObstacleDebrisDamping,
                        ObstacleDebrisMinScale, ObstacleDebrisMaxScale);
                }

                Destroy(gameObject);
            }
        }
    }
}
