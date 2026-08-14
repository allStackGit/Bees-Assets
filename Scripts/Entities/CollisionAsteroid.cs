using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public partial class CollisionAsteroid : Obstacle
    {
        public Rigidbody2D Body;
        public int Speed, SizeClass;
        public GameObject ExplosionAnimation;
        /// <summary>
        /// The spawned asteroid explosion animation
        /// </summary>
        public AsteroidExplosionAnimation AsteroidExplosionAnimation;
        public bool HasCollisionAnimation, HasCrackedSprite;
        public bool HasDroppedDestructionAnimation, HasTouchedMapBorder, HasEnteredMap;
        public bool IsShard;
        public Sprite OriginalSprite, CrackedSprite;
        public bool IsDelayKilled;

        public override void Create(Stage stage)
        {
            Health = ConfigData.CollisionAsteroidHealthIncrement * SizeClass;
            base.Create(stage);
            Speed = Utilities.RandomInt(Stage.AsteroidMaxSpeed) + ConfigData.MinimumAsteroidSpeed;
            if (!Stage.IsTraining && HasCollisionAnimation)
            {
                AsteroidExplosionAnimation = Instantiate(ExplosionAnimation, Vector2.zero, Quaternion.identity).GetComponent<AsteroidExplosionAnimation>();
                AsteroidExplosionAnimation.gameObject.SetActive(false);
            }
            else
            {
                HasCollisionAnimation = false;
            }
        }

        public override void Setup(Level level)
        {
            base.Setup(level);
            transform.parent = Level.Map.Transform;
            Level.State.AddObstacle(this);
            MapPointsIndex = Level.Pathfinder.AddObstacle(this);

            _randomPoint = new Vector2(
                Utilities.RandomSign() * (Level.HalfMapWidth + ConfigData.MinimumAsteroidSpawnDistance),
                Utilities.RandomSign() * Utilities.RandomInt(Level.HalfMapHeight));

            if (Utilities.CoinToss())
            {
                _randomPoint = new Vector2(
                    Utilities.RandomSign() * Utilities.RandomInt(Level.HalfMapWidth),
                    Utilities.RandomSign() * (Level.HalfMapHeight + ConfigData.MinimumAsteroidSpawnDistance));
            }
            transform.localPosition = _randomPoint;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));
            SetMoving();
        }

        public override void ClearData()
        {
            base.ClearData();
            NearbyShips.Clear();
            TouchingShips.Clear();
            NearbyObstacles.Clear();
            AsteroidsHit.Clear();
            ShardFamily.Clear();
            LastHitAsteroid = null;
            HasTouchedMapBorder = false;
            HasDroppedDestructionAnimation = false;
            HasEnteredMap = false;
            IsDelayKilled = false;
            SpriteRenderer.sprite = OriginalSprite;
        }

        private Vector2 _randomPoint;
        public void SetMoving()
        {
            _randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.linearVelocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), _randomPoint);
            Body.angularVelocity = Speed * Utilities.RandomFloat(ConfigData.MinimumAsteroidAngularSpeedMultiplier);
        }

        protected ScaledTimer _delayKillTimer = new ScaledTimer();
        private ScaledTimer _collisionAnimation = new ScaledTimer();

        /// <summary>
        /// When the asteroid got killed in a collision with another asteroid. This calls the asteroid explosion animation, and spawns the asteroid pieces.
        /// A little bit later but before the animation finishes, the asteroids are killed.
        /// </summary>
        public void GotKilledInCollision()
        {
            SwitchToCrackedSprite();

            if (LastHitAsteroid != null && !LastHitAsteroid.HasDroppedDestructionAnimation &&
                (SizeClass >= LastHitAsteroid.SizeClass || LastHitAsteroid.Health > 0))
            {
                HasDroppedDestructionAnimation = true;
                _collisionAnimation.Reuse(.25f, ShowCollisionAnimation);
                Level.AddTimer(_collisionAnimation);
            }

            if (!IsDelayKilled)
            {
                IsDelayKilled = true;
                _delayKillTimer.Reuse(.35f, DelayKill);
                Level.AddTimer(_delayKillTimer);
            }
        }

        /// <summary>
        /// Just like GotKilledInCollision except it always spawns the explosion because it didn't hit another asteroid.
        /// </summary>
        public void GotKilledInShipCollision()
        {
            SwitchToCrackedSprite();

            HasDroppedDestructionAnimation = true;
            _collisionAnimation.Reuse(.15f, ShowCollisionAnimation);
            Level.AddTimer(_collisionAnimation);

            if (!IsDelayKilled)
            {
                IsDelayKilled = true;
                _delayKillTimer.Reuse(.35f, DelayKill);
                Level.AddTimer(_delayKillTimer);
            }
        }

        public bool CheckForCrackedSprite()
        {
            return HasCrackedSprite && (float)Health / OriginalHealth < .5f;
        }

        public void SwitchToCrackedSprite()
        {
            SpriteRenderer.sprite = CrackedSprite;
        }

        private void DelayKill()
        {
            Kill(false);
        }

        private Vector2 _asteroidPosition;
        private void ShowCollisionAnimation()
        {
            if (HasCollisionAnimation)
            {
                _asteroidPosition = GetPosition();
                if (LastHitAsteroid != null)
                {
                    _asteroidPosition -= (_asteroidPosition - LastHitAsteroid.GetPosition()) / 2;
                }
                AsteroidExplosionAnimation.transform.SetParent(Level.Map.Transform);
                AsteroidExplosionAnimation.transform.localPosition = _asteroidPosition;
                AsteroidExplosionAnimation.Asteroid = this;
                AsteroidExplosionAnimation.gameObject.SetActive(true);
                AsteroidExplosionAnimation.name = $"{Name} collision explosion animation";
            }
        }

        public void Kill(bool endKill)
        {
            if (!IsDead)
            {
                IsDead = true;
                if (!endKill)
                {
                    if (!HasDroppedDestructionAnimation)
                    {
                        foreach (Ship ship in NearbyShips)
                        {
                            ship.LeftNearbyAsteroid(this);
                        }
                    }
                    SpawnBreakAwayAsteroids();
                }
                Level.State.RemoveObstacle(this);
                Level.CancelTimer(_delayKillTimer);
                Level.CancelTimer(_collisionAnimation);
                gameObject.SetActive(false);

                // Shards have their own prefab/pool. Returning them through the ordinary
                // asteroid release queue contaminates CollisionAsteroidPool and eventually
                // makes GetCollisionAsteroidFromPool() return a shard prefab. Return shards
                // directly to their owning pool; full asteroids continue through State.Release.
                if (IsShard)
                {
                    Stage.Pool.ReturnCollisionAsteroidShardToPool(this);
                }
                else
                {
                    Level.State.AsteroidsToRelease.Add(this);
                }
            }
        }

        private CollisionAsteroid _asteroidShard;
        private readonly List<CollisionAsteroid> _shardFamily = new List<CollisionAsteroid>();
        private int _asteroidCount;
        private int _pieceCount;
        private int _loopIndex;

        public void SpawnBreakAwayAsteroids()
        {
            _shardFamily.Clear();
            _asteroidCount = SizeClass < 6 ? 0 : (SizeClass > 6 ? 3 : 2);
            _pieceCount = (int)(SizeClass * 1.5f);

            for (_loopIndex = 0; _loopIndex < _asteroidCount; _loopIndex++)
            {
                _asteroidShard = Stage.Pool.GetCollisionAsteroidShardFromPool();
                _asteroidShard.IsShard = true;
                _asteroidShard.Setup(Level);
                _asteroidShard.transform.localPosition = GetPosition();
                _asteroidShard.Body.angularVelocity = Body.angularVelocity;
                _asteroidShard.HasEnteredMap = true;
                _asteroidShard.HasTouchedMapBorder = true;
                _shardFamily.Add(_asteroidShard);
            }

            for (int shardIndex = 0; shardIndex < _shardFamily.Count; shardIndex++)
            {
                CollisionAsteroid shard = _shardFamily[shardIndex];
                shard.ShardFamily.Clear();
                for (int familyIndex = 0; familyIndex < _shardFamily.Count; familyIndex++)
                {
                    if (familyIndex != shardIndex)
                    {
                        shard.ShardFamily.Add(_shardFamily[familyIndex]);
                    }
                }
            }

            for (_loopIndex = 0; _loopIndex < _pieceCount; _loopIndex++)
            {
                Stage.Pool.GetAsteroidPieceFromPool().Setup(Level, this);
            }
        }
    }
}