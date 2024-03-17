using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class MapBorder : Obstacle
    {

        private void OnTriggerExit2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Obstacle"))
            {
                CollisionAsteroid asteroid = collidingThing.GetComponent<CollisionAsteroid>();
                if (asteroid.HasEnteredMap)
                {
                    //Debug.Log($"{asteroid.Name} left the map border and is being killed");
                    asteroid.Kill();
                }
                else
                {
                    //Debug.Log($"{asteroid.Name} entered the map border");
                    asteroid.HasEnteredMap = true;
                }


            }
        }


    }
}