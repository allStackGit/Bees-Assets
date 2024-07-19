using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class CollisionAsteroid : Obstacle
    {
        public Rigidbody2D Body;
        public int Speed; 
        public HashSet<Ship> NearbyShips = new HashSet<Ship>();
        public HashSet<Ship> TouchingShips = new HashSet<Ship>();
        public HashSet<Obstacle> NearbyObstacles = new HashSet<Obstacle>();
        public Obstacle CollisionObstacle;
        // Use this for initialization
        public new void Setup(LevelStage level, int id)
        {
            base.Setup(level, id);
            Speed = Utilities.RandomInt(Level.AsteroidMaxSpeed)+2;

            // starting right (+) or left (-)
            Vector2 randomPosition = new Vector2(Utilities.RandomSign() * (Level.HalfMapWidth + 100), (Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapHeight))));
            
            if (Utilities.CoinToss()) // start top / bottom instead
            {
                randomPosition = new Vector2((Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapWidth))), Utilities.RandomSign() * (Level.HalfMapHeight + 100));
            }
            transform.localPosition = randomPosition;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));
            IsMobile = true;

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            SetMoving();
        }
        public void SetMoving()
        {
            Vector2 randomPoint = Utilities.RandomCoordinate(Level, Level.GetPosition(), new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.velocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), randomPoint);
            Body.angularVelocity = Speed * Utilities.RandomFloat(1);
        }

        private void OnTriggerEnter2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (NearbyShips.Contains(ship))
                {
                    //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                    TouchingShips.Add(ship);
                    ship.Kill(null);
                }
                else
                {
                    ship.FoundNearbyAsteroid(this);
                    NearbyShips.Add(ship);
                    //Debug.Log($"{ship.Name} is near {Name}");
                }
            }
            else if (collidingThing.CompareTag("Obstacle"))
            {
                Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
                if (NearbyObstacles.Contains(obstacle))
                {
                    //Debug.Log($"It looks like {ship.Name} was already nearby and hit {Name}");
                    //ship.Kill(null);
                    CollisionObstacle = obstacle;
                    Invoke(nameof(DelayedCollision), 1);
                }
                else if (obstacle.IsMobile && obstacle.HasEnteredMap)
                {
                    NearbyObstacles.Add(obstacle);
                    //Debug.Log($"{ship.Name} is near {Name}");
                }
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
            if (CollisionObstacle != null)
            {
                CollisionObstacle.Kill();
                Kill();
            }

        }
    }
}