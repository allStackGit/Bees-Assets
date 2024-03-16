using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class MapBorder : Obstacle
    {

        // Use this for initialization
        void Start()
        {
            IsMapBorder = true;
        }

        private void OnTriggerExit2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Obstacle"))
            {
                CollisionAsteroid asteroid = collidingThing.GetComponent<CollisionAsteroid>();
                Debug.Log($"{asteroid.Name} left the map border and is being killed");
                asteroid.Kill();

            }
        }


    }
}