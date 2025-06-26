using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Assets.Scripts.Entities
{
    public class CollisionAsteroid : Obstacle
    {
        public Rigidbody2D Body;
        public int Speed, SizeClass; 
        public HashSet<Ship> NearbyShips = new HashSet<Ship>();
        public HashSet<Ship> TouchingShips = new HashSet<Ship>();
        public HashSet<Obstacle> NearbyObstacles = new HashSet<Obstacle>();
        public HashSet<CollisionAsteroid> AsteroidsHit = new HashSet<CollisionAsteroid>();
        /// <summary>
        /// If this asteroid is a shard then it has a shard family of all the other shards that were spawned from the same asteroid and can't collide with each other
        /// </summary>
        public HashSet<CollisionAsteroid> ShardFamily = new HashSet<CollisionAsteroid>();
        public CollisionAsteroid LastHitAsteroid;
        public GameObject ExplosionAnimation;
        /// <summary>
        /// The spawned asteroid explosion animation
        /// </summary>
        public AsteroidExplosionAnimation AsteroidExplosionAnimation;
        public bool HasCollisionAnimation, HasCrackedSprite;
        public bool HasDroppedDestructionAnimation, HasTouchedMapBorder, HasEnteredMap;
        public Sprite OriginalSprite, CrackedSprite;

        //public string OriginalName; // [debug]
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
            //OriginalName = gameObject.name;

        }
        // Use this for initialization
        public override void Setup(Level level)
        {
            base.Setup(level);
            transform.parent = Level.Map.Transform;
            Level.State.AddObstacle(this);
            MapPointsIndex = Level.Pathfinder.AddObstacle(this);


            // starting right (+) or left (-)
            _randomPoint = new Vector2(Utilities.RandomSign() * (Level.HalfMapWidth + ConfigData.MinimumAsteroidSpawnDistance), (Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapHeight))));
            
            if (Utilities.CoinToss()) // start top / bottom instead
            {
                _randomPoint = new Vector2((Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapWidth))), Utilities.RandomSign() * (Level.HalfMapHeight + ConfigData.MinimumAsteroidSpawnDistance));
            }
            transform.localPosition = _randomPoint;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            SetMoving();


            //Invoke(nameof(RemoveImmunity), 4);
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

        Vector2 _randomPoint;
        public void SetMoving()
        {
            _randomPoint = Utilities.RandomCoordinate(Level, Level.GetPosition(), new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.linearVelocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), _randomPoint);
            Body.angularVelocity = Speed * Utilities.RandomFloat(ConfigData.MinimumAsteroidAngularSpeedMultiplier);
        }
        protected ScaledTimer _delayKillTimer = new ScaledTimer();
        public void ShipCollision(Ship ship)
        {
            if (NearbyShips.Contains(ship) && Health > 0)
            {
                //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                TouchingShips.Add(ship);

                // kill the ship, damage the asteroid
                if (ship.SizeClass < SizeClass)
                {
                    Health -= math.min(ship.OriginalHealth, Health);
                    ship.LogDamage(ship.Health); // kills the ship but logs the damage and tsv change first

                }
                else if (ship.SizeClass == SizeClass) { // kill both ship and asteroid
                    ship.LogDamage(ship.Health); // kills the ship but logs the damage and tsv change first
                    Health = 0;
                }
                else // kill the asteroid, damage the ship
                {
                    ship.LogDamage(OriginalHealth);
                    Health = 0;
                }
                NearbyShips.Remove(ship);

                if (Health == 0)
                {
                    GotKilledInShipCollision();
                    //Invoke(nameof(DelayKill), ConfigData.CollisionAsteroidKillDelay);
                }
                else if (CheckForCrackedSprite())
                {
                    SwitchToCrackedSprite();
                }
            }
            else
            {
                ship.FoundNearbyAsteroid(this);
                NearbyShips.Add(ship);
                //Debug.Log($"{ship.Name} is near {Name}");
            }
        }

        public void ObstacleCollision(Obstacle obstacle)
        {
            if (NearbyObstacles.Contains(obstacle) && HasEnteredMap && Health > 0)
            {
                //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                //ship.Kill(null);
                LastHitAsteroid = (CollisionAsteroid)obstacle;
                if (LastHitAsteroid.HasTouchedMapBorder && LastHitAsteroid.Health > 0 && !ShardFamily.Contains(LastHitAsteroid))
                {
                    //Debug.Log($"It looks like {LastHitAsteroid.Name} hit {Name}");
                    AsteroidsHit.Add(LastHitAsteroid);
                    if (LastHitAsteroid.AsteroidsHit.Contains(this))
                    {
                        //Debug.Log($"{LastHitAsteroid.Name} has already registered the hit against {Name}");
                        return;
                    }
                    //Debug.Log($"{Name} ({SizeClass}) has been hit by {LastHitAsteroid.Name} ({LastHitAsteroid.SizeClass}) and will take {LastHitAsteroid.Health} damage against {Health}");
                    if (LastHitAsteroid.SizeClass < SizeClass) // kill the other asteroid, damage this asteroid
                    {

                        Health -= math.min(LastHitAsteroid.OriginalHealth, Health);
                        LastHitAsteroid.Health = 0;

                    }
                    else if (LastHitAsteroid.SizeClass == SizeClass) // kill both asteroids
                    {
                        Health = 0;
                        LastHitAsteroid.Health = 0;
                    }
                    else // kill this asteroid, damage the other asteroid
                    {
                        LastHitAsteroid.Health -= math.min(OriginalHealth, LastHitAsteroid.Health);
                        Health = 0;

                    }


                    if (LastHitAsteroid.Health == 0)
                    {
                        LastHitAsteroid.LastHitAsteroid = this;
                        LastHitAsteroid.GotKilledInCollision();
                        //LastHitAsteroid.Invoke(nameof(DelayKill), ConfigData.CollisionAsteroidKillDelay);
                    }
                    else if (LastHitAsteroid.CheckForCrackedSprite())
                    {
                        LastHitAsteroid.SwitchToCrackedSprite();
                    }
                    if (Health == 0)
                    {
                        GotKilledInCollision();
                        LastHitAsteroid = null;
                    }
                    else if (CheckForCrackedSprite())
                    {
                        SwitchToCrackedSprite();
                        LastHitAsteroid = null;
                    }
                    else
                    {
                        LastHitAsteroid = null;
                    }
                }
                else
                {
                    LastHitAsteroid = null;
                }


            }
            else if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
            {
                NearbyObstacles.Add(obstacle);
                Debug.Log($"{Name} is near {obstacle.Name}");
            }
            //else if (IsImmune) // [debug]
            //{
            //    Debug.Log($"{Name} is immune and ignored the collision with {obstacle.Name}");
            //}
            //else if (!HasEnteredMap) // [debug]
            //{
            //    Debug.Log($"{Name} has not entered the map and ignored the collision with {obstacle.Name}");
            //}
            //else if (IsDead)
            //{
            //    Debug.Log($"{Name} is dead and ignored the collision with {obstacle.Name}");
            //}
        }
        ScaledTimer _collisionAnimation = new ScaledTimer();
        /// <summary>
        /// When the asteroid got killed in a collision with another asteroid. This calls the asteroid explosion animation, and spawns the asteroid pieces.
        /// A little bit later but before the animation finishes, the asteroids are killed
        /// </summary>
        public void GotKilledInCollision()
        {
            SwitchToCrackedSprite();


            if (LastHitAsteroid != null && !LastHitAsteroid.HasDroppedDestructionAnimation && (SizeClass >= LastHitAsteroid.SizeClass || LastHitAsteroid.Health > 0))
            {
                HasDroppedDestructionAnimation = true;

                _collisionAnimation.Reuse(.25f, ShowCollisionAnimation);
                Level.AddTimer(_collisionAnimation);
                //ShowCollisionAnimation();
            }
            //else
            //{
            //    Debug.Log($"{Name} did not drop a collision explosion when it died");
            //    Debug.Log(LastHitAsteroid);
            //    Debug.Log(LastHitAsteroid?.HasDroppedDestructionAnimation);
            //    Debug.Log(SizeClass);
            //    Debug.Log(LastHitAsteroid?.SizeClass);
            //    Debug.Log(LastHitAsteroid?.Health);
            //}
            if (!IsDelayKilled)
            {
                IsDelayKilled = true;
                _delayKillTimer.Reuse(.35f, DelayKill);
                Level.AddTimer(_delayKillTimer);

            }


        }

        /// <summary>
        /// Just like got killed in collision except it always spawns the explosion because it didn't hit another asteroid
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

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            Debug.Log($"{Name} collided with {collider.name} belonging to {collider.transform.parent.name}");
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                ShipCollision(_collidingThing.GetComponent<Ship>());
            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                ObstacleCollision(_collidingThing.GetComponent<Obstacle>());
                
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

        GameObject _collidingThing;
        Ship _collidingShip;
        Obstacle _collidingObstacle;
        private void OnTriggerExit2D(Collider2D collider)
        {
            //Debug.Log($"{Asteroid.Name} proximity collided");
            _collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (_collidingThing.CompareTag("Ship"))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (TouchingShips.Contains(_collidingShip))
                {
                    //Debug.Log($"{ship.Name} is no longer touching {Name}");
                    TouchingShips.Remove(_collidingShip);
                }
                else if (NearbyShips.Contains(_collidingShip))
                {
                    NearbyShips.Remove(_collidingShip);
                    _collidingShip.LeftNearbyAsteroid(this);
                    //Debug.Log($"{ship.Name} left {Name}");
                }


            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                _collidingObstacle = _collidingThing.GetComponent<Obstacle>();
                if (NearbyObstacles.Contains(_collidingObstacle))
                {
                    NearbyObstacles.Remove(_collidingObstacle);
                }
            }
        }

        private void DelayKill()
        {
            Kill(false);
        }

        Vector2 _asteroidPosition;
        /// <summary>
        /// Displays the asteroid destruction animation if it has one and kills the asteroid
        /// </summary>
        private void ShowCollisionAnimation()
        {
            if (HasCollisionAnimation)
            {
                //GameObject explosion = Instantiate(ExplosionAnimation, Vector2.zero, Quaternion.identity);
                //explosion.transform.parent = Level.Map.Transform;
                //explosion.transform.localPosition = GetPosition();
                //AsteroidExplosionAnimation asteroidExplosionAnimation = explosion.GetComponent<AsteroidExplosionAnimation>();
                //asteroidExplosionAnimation.Asteroid = this;
                //HasDroppedDestructionAnimation = true;
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
                        NearbyShips.ToList().ForEach((ship) =>
                        {
                            ship.LeftNearbyAsteroid(this);
                        });
                    }
                    SpawnBreakAwayAsteroids();

                }
                Level.State.RemoveObstacle(this);
                //Debug.Log($"Killing and returning {Name} to the pool");
                Level.State.AsteroidsToRelease.Add(this);
                //Stage.Pool.ReturnCollisionAsteroidToPool(this);
                Level.CancelTimer(_delayKillTimer);
                Level.CancelTimer(_collisionAnimation);
                gameObject.SetActive(false);
            }
            //else
            //{
            //    Debug.LogWarning($"Tried to kill already dead asteroid {Name}");
            //}
        }

        CollisionAsteroid _asteroidShard;
        List<CollisionAsteroid> _shardFamily = new List<CollisionAsteroid>();
        int _asteroidCount;
        int _pieceCount;
        int _loopIndex;
        public void SpawnBreakAwayAsteroids()
        {
            _shardFamily.Clear();
            _asteroidCount = SizeClass < 6 ? 0 : (SizeClass > 6 ? 3 : 2);
            _pieceCount = (int)(SizeClass * 1.5f);

            //Debug.Log($"{Name} died and spawned {asteroidCount} asteroids and {pieceCount} pieces");

            for (_loopIndex = 0; _loopIndex < _asteroidCount; _loopIndex++)
            {
                _asteroidShard = Stage.Pool.GetCollisionAsteroidShardFromPool();
                _asteroidShard.Setup(Level);
                _asteroidShard.transform.localPosition = GetPosition();
                _asteroidShard.Body.angularVelocity = Body.angularVelocity;
                _asteroidShard.HasEnteredMap = true;
                _asteroidShard.HasTouchedMapBorder = true;

                _asteroidShard.Name = $"{_asteroidShard.Name}  - Shard"; // [debug]
                _shardFamily.Add(_asteroidShard);
            }

            _shardFamily.ForEach((shard) =>
            {
                shard.ShardFamily = new HashSet<CollisionAsteroid>(_shardFamily); 
                shard.ShardFamily.Remove(shard);
            });

            for (_loopIndex = 0; _loopIndex < _pieceCount; _loopIndex++)
            {
                Stage.Pool.GetAsteroidPieceFromPool().Setup(Level, this);
            }
        }
        //private void FixedUpdate()
        //{
        //    if (!IsDead && !IsDelayKilled && Health == 0)
        //    {
        //        Debug.LogError($"{Name} is going around with 0 health");
        //    }
        //}

    }
}