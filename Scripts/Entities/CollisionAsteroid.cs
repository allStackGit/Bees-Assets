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
        // Use this for initialization
        public void Setup(LevelStage level, int id, Vector2 position)
        {
            base.Setup(level, id);
            transform.position = position;
            Speed = Utilities.RandomInt(20);
            Body.velocity = Speed * new Vector2(Utilities.RandomFloat(1), Utilities.RandomFloat(1));
            Body.angularVelocity = Speed * Utilities.RandomFloat(1);
            IsMobile = true;

            Debug.Log($"Setup Asteroid {Name} with velocity: {Body.velocity} and angular velocity: {Body.angularVelocity}");
        }

        private void OnTriggerEnter2D(Collider2D collider)
        {
            Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (NearbyShips.Contains(ship))
                {
                    Debug.Log($"It looks like {ship.Name} hit {Name}");
                }
                else
                {
                    NearbyShips.Add(ship);
                    Debug.Log($"{ship.Name} is near {Name}");
                }

            }
        }
    }
}