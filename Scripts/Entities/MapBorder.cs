using Assets.Scripts.Entities.Ships;
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
                    asteroid.Kill(true);
                }
                else
                {
                    //Debug.Log($"{asteroid.Name} entered the map border");
                    asteroid.HasEnteredMap = true;
                }


            }
        }

        private void OnTriggerEnter2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                //Debug.Log("Hit by ship");
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship.HasTargetDirection)
                {
                    //Debug.Log($"{Name} hit the map border while moving in a direction");
                    if (ship.ShipType == "Barge")
                    {
                        Barge barge = (Barge)ship;
                        if (barge.IsCharging)
                        {
                            StartCoroutine(barge.StopCharge());
                            return;
                        }
                    }
                    ship.StopMoving("Hit map border");
                }
            }
            
        }


    }
}