using Assets.Scripts.Entities.Ships;
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
        public Sprite CrackedSprite;
        public SpriteRenderer SpriteRenderer;
        // Use this for initialization
        public new void Setup(LevelStage level, int id)
        {
            base.Setup(level, id);
            Health = ConfigData.CollisionAsteroidHealthIncrement * SizeClass;
            OriginalHealth = Health;
            Speed = Utilities.RandomInt(Level.AsteroidMaxSpeed) + ConfigData.MinimumAsteroidSpeed;

            // starting right (+) or left (-)
            Vector2 randomPosition = new Vector2(Utilities.RandomSign() * (Level.HalfMapWidth + ConfigData.MinimumAsteroidSpawnDistance), (Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapHeight))));
            
            if (Utilities.CoinToss()) // start top / bottom instead
            {
                randomPosition = new Vector2((Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapWidth))), Utilities.RandomSign() * (Level.HalfMapHeight + ConfigData.MinimumAsteroidSpawnDistance));
            }
            transform.localPosition = randomPosition;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));
            IsCollisionAsteroid = true;

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            SetMoving();

            if (ExplosionAnimation != null)
            {
                HasCollisionAnimation = true;
            }
            if (CrackedSprite != null)
            {
                HasCrackedSprite = true;
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
                    Invoke(nameof(DelayedCollision), ConfigData.CollisionAsteroidKillDelay);
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
            if (NearbyObstacles.Contains(obstacle))
            {
                //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                //ship.Kill(null);
                LastHitAsteroid = (CollisionAsteroid)obstacle;
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
                    LastHitAsteroid.Invoke(nameof(DelayedCollision), ConfigData.CollisionAsteroidKillDelay);
                }
                else if (LastHitAsteroid.HasCrackedSprite && (float)LastHitAsteroid.Health / LastHitAsteroid.OriginalHealth < .5f)
                {
                    LastHitAsteroid.SpriteRenderer.sprite = LastHitAsteroid.CrackedSprite;
                }
                if (Health == 0)
                {
                    SpriteRenderer.sprite = CrackedSprite;
                    Invoke(nameof(DelayedCollision), ConfigData.CollisionAsteroidKillDelay);
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
            else if (obstacle.IsCollisionAsteroid && obstacle.HasEnteredMap)
            {
                NearbyObstacles.Add(obstacle);
                //Debug.Log($"{ship.Name} is near {Name}");
            }
        }

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
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

        private void DelayedCollision()
        {
            Kill();
        }

        private void ShowCollisionAnimation()
        {
            if (!Level.IsTraining && HasCollisionAnimation)
            {
                GameObject explosion = Instantiate(ExplosionAnimation, Vector2.zero, Quaternion.identity);
                explosion.transform.parent = Level.Map.transform;
                explosion.transform.localPosition = GetPosition();
            }

        }

        public override void Kill()
        {
            NearbyShips.ToList().ForEach((ship) =>
            {
                ship.LeftNearbyAsteroid(this);
            });
            ShowCollisionAnimation();
            base.Kill();
        }
    }
}