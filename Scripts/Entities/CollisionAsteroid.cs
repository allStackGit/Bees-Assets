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
        public CollisionAsteroid LastHitAsteroid;
        public GameObject ExplosionAnimation;
        public bool HasCollisionAnimation, HasCrackedSprite;
        public bool HasDroppedDestructionAnimation, IsImmune, HasTouchedMapBorder, HasEnteredMap;
        public Sprite CrackedSprite;
        public SpriteRenderer SpriteRenderer;

        private int _overlaps;
        private bool _isColliding => _overlaps > 0;

        public override void Create(Stage stage)
        {
            Health = ConfigData.CollisionAsteroidHealthIncrement * SizeClass;
            base.Create(stage);
            Speed = Utilities.RandomInt(Stage.AsteroidMaxSpeed) + ConfigData.MinimumAsteroidSpeed;

            if (ExplosionAnimation != null)
            {
                HasCollisionAnimation = true;
            }
            if (CrackedSprite != null)
            {
                HasCrackedSprite = true;
            }

        }
        // Use this for initialization
        public new void Setup(Level level)
        {
            base.Setup(level);

            // starting right (+) or left (-)
            Vector2 randomPosition = new Vector2(Utilities.RandomSign() * (Level.HalfMapWidth + ConfigData.MinimumAsteroidSpawnDistance), (Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapHeight))));
            
            if (Utilities.CoinToss()) // start top / bottom instead
            {
                randomPosition = new Vector2((Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapWidth))), Utilities.RandomSign() * (Level.HalfMapHeight + ConfigData.MinimumAsteroidSpawnDistance));
            }
            transform.localPosition = randomPosition;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            SetMoving();


            IsImmune = true;
            Invoke(nameof(RemoveImmunity), 4);
        }
        public override void ClearData()
        {
            base.ClearData();
            NearbyShips.Clear();
            TouchingShips.Clear();
            NearbyObstacles.Clear();
            AsteroidsHit.Clear();
            LastHitAsteroid = null;
            HasTouchedMapBorder = false;
            HasDroppedDestructionAnimation = false;
            HasEnteredMap = false;
        }

        public void RemoveImmunity()
        {
            IsImmune = false;
            if (_isColliding)
            {
                NearbyObstacles.ToList().ForEach((obstacle) =>
                {
                    if (Collider.IsTouching(obstacle.Collider))
                    {
                        ObstacleCollision(obstacle);
                    }
                });
            }
        }
        public void SetMoving()
        {
            Vector2 randomPoint = Utilities.RandomCoordinate(Level, Level.GetPosition(), new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.velocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), randomPoint);
            Body.angularVelocity = Speed * Utilities.RandomFloat(ConfigData.MinimumAsteroidAngularSpeedMultiplier);
        }

        public override void ShipCollision(Ship ship)
        {
            if (NearbyShips.Contains(ship))
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
                    SpriteRenderer.sprite = CrackedSprite;
                    Invoke(nameof(DelayKill), ConfigData.CollisionAsteroidKillDelay);
                }
                else if (HasCrackedSprite && (float) Health / OriginalHealth < .5f)
                {
                    SpriteRenderer.sprite = CrackedSprite;
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
            if (NearbyObstacles.Contains(obstacle) && !IsImmune && HasEnteredMap)
            {
                //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                //ship.Kill(null);
                LastHitAsteroid = (CollisionAsteroid)obstacle;
                if (!LastHitAsteroid.IsImmune && LastHitAsteroid.HasTouchedMapBorder)
                {
                    //Debug.Log($"It looks like {LastHitAsteroid.Name} was already nearby and hit {Name}");
                    AsteroidsHit.Add(LastHitAsteroid);
                    if (LastHitAsteroid.AsteroidsHit.Contains(this))
                    {
                        //Debug.Log($"{asteroid.Name} has already registered the hit against {Name}");
                        return;
                    }
                    //Debug.Log($"{Name} ({SizeClass}) has been hit by {asteroid.Name} ({asteroid.SizeClass}) and will take {asteroid.Health} damage against {Health}");
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
                        LastHitAsteroid.SpriteRenderer.sprite = LastHitAsteroid.CrackedSprite;
                        LastHitAsteroid.Invoke(nameof(DelayKill), ConfigData.CollisionAsteroidKillDelay);
                    }
                    else if (LastHitAsteroid.HasCrackedSprite && (float)LastHitAsteroid.Health / LastHitAsteroid.OriginalHealth < .5f)
                    {
                        LastHitAsteroid.SpriteRenderer.sprite = LastHitAsteroid.CrackedSprite;
                    }
                    if (Health == 0)
                    {
                        SpriteRenderer.sprite = CrackedSprite;
                        Invoke(nameof(DelayKill), ConfigData.CollisionAsteroidKillDelay);
                    }
                    else if (HasCrackedSprite && (float)Health / OriginalHealth < .5f)
                    {
                        SpriteRenderer.sprite = CrackedSprite;
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
                //Debug.Log($"{ship.Name} is near {Name}");
            }
        }

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            _overlaps++;
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                ShipCollision(collidingThing.GetComponent<Ship>());
            }
            else if (collidingThing.CompareTag("Obstacle"))
            {
                ObstacleCollision(collidingThing.GetComponent<Obstacle>());
                
            }
        }

        private void OnTriggerExit2D(Collider2D collider)
        {
            _overlaps--;
            //Debug.Log($"{Asteroid.Name} proximity collided");
            GameObject collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (TouchingShips.Contains(ship))
                {
                    //Debug.Log($"{ship.Name} is no longer touching {Name}");
                    TouchingShips.Remove(ship);
                }
                else if (NearbyShips.Contains(ship))
                {
                    NearbyShips.Remove(ship);
                    ship.LeftNearbyAsteroid(this);
                    //Debug.Log($"{ship.Name} left {Name}");
                }


            }
            else if (collidingThing.CompareTag("Obstacle"))
            {
                Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
                if (NearbyObstacles.Contains(obstacle))
                {
                    NearbyObstacles.Remove(obstacle);
                }
            }
        }

        private void DelayKill()
        {
            Kill(false);
        }

        /// <summary>
        /// Displays the asteroid destruction animation if it has one and kills the asteroid
        /// </summary>
        private void ShowCollisionAnimation()
        {
            if (!Level.Stage.IsTraining && HasCollisionAnimation)
            {
                GameObject explosion = Instantiate(ExplosionAnimation, Vector2.zero, Quaternion.identity);
                explosion.transform.parent = Level.Map.transform;
                explosion.transform.localPosition = GetPosition();
                AsteroidExplosionAnimation asteroidExplosionAnimation = explosion.GetComponent<AsteroidExplosionAnimation>();
                asteroidExplosionAnimation.Asteroid = this;
                HasDroppedDestructionAnimation = true;
            }
            else
            {
                Kill(false);
            }

        }

        public void Kill(bool endKill)
        {
            if (!IsDead)
            {
                IsDead = true;
                if (!endKill && !HasDroppedDestructionAnimation)
                {
                    NearbyShips.ToList().ForEach((ship) =>
                    {
                        ship.LeftNearbyAsteroid(this);
                    });
                }
                Level.State.RemoveObstacle(this);
                Debug.Log($"Returning {Name} to the pool");
                Stage.Pool.ReturnCollisionAsteroidToPool(this);
            }
            else
            {
                Debug.Log($"Tried to kill already dead asteroid {Name}");
            }
        }

        public void SpawnBreakAwayAsteroids()
        {
            int asteroidCount = SizeClass < 6 ? 0 : (SizeClass > 6 ? 3 : 2);
            int pieceCount = (int) (SizeClass * 1.5f);

            //Debug.Log($"{Name} died and spawned {asteroidCount} asteroids and {pieceCount} pieces");

            for (int i = 0; i < asteroidCount; i++)
            {
                GameObject instance = Instantiate(Level.Stage.Prefabs.BreakawayAsteroids[Utilities.RandomInt(Level.Stage.Prefabs.BreakawayAsteroids.Count)]);
                CollisionAsteroid asteroid = Level.AddAsteroid(instance);
                asteroid.transform.localPosition = GetPosition();
                asteroid.Body.angularVelocity = Body.angularVelocity;
                asteroid.HasEnteredMap = true;

            }

            for (int i = 0; i < pieceCount; i++)
            {
                GameObject instance = Instantiate(Level.Stage.Prefabs.AsteroidPieces[Utilities.RandomInt(Level.Stage.Prefabs.AsteroidPieces.Count)]);
                instance.transform.parent = Level.Map.transform;
                AsteroidPiece asteroid = instance.GetComponent<AsteroidPiece>();
                Level.State.AddObstacle(asteroid);
                asteroid.Setup(Level, this);

            }
        }
    }
}