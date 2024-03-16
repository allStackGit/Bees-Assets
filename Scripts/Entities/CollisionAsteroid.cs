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
        public GameObject WarningColliderObject;
        // Use this for initialization
        public new void Setup(LevelStage level, int id)
        {
            base.Setup(level, id);
            Speed = Utilities.RandomInt(Level.AsteroidMaxSpeed);
            int directionSign = Utilities.RandomSign();

            // starting right (+) or left (-)
            Vector2 randomPosition = new Vector2(directionSign * (Level.HalfMapWidth + 100), (Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapHeight))));
            
            if (directionSign > 0) // start top / bottom instead
            {
                randomPosition = new Vector2((Utilities.RandomSign() * (Utilities.RandomInt(Level.HalfMapWidth))), directionSign * (Level.HalfMapHeight + 100));
            }
            transform.localPosition = randomPosition;
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));
            IsMobile = true;

            WarningColliderObject.GetComponent<ProximityWarning>().Setup(this);
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
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                ship.Kill(null);
                //Debug.Log($"It looks like {ship.Name} hit {Name}");

            }
        }
    }
}