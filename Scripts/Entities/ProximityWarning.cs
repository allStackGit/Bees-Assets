using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Xml.Linq;
using UnityEngine;

/*
 * It seems this class is no longer needed
 * 
 * */
namespace Assets.Scripts.Entities
{
    public class ProximityWarning : MonoBehaviour
    {
        public LevelStage Level;
        public CollisionAsteroid Asteroid;
        public void Setup(CollisionAsteroid asteroid)
        {
            Asteroid = asteroid;
            Level = Asteroid.Level;
        }
        //private void OnTriggerEnter2D(Collider2D collider)
        //{
        //    //Debug.Log($"{Asteroid.Name} proximity collided");
        //    GameObject collidingThing = collider.gameObject;
        //    //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
        //    if (collidingThing.CompareTag("Ship"))
        //    {
        //        Ship ship = collidingThing.GetComponent<Ship>();
        //        ship.FoundNearbyAsteroid(Asteroid);
        //        Asteroid.NearbyShips.Add(ship);
        //        Debug.Log($"{ship.Name} is near {Asteroid.Name}");

        //    }
        //}

        //private void OnTriggerExit2D(Collider2D collider)
        //{
        //    //Debug.Log($"{Asteroid.Name} proximity collided");
        //    GameObject collidingThing = collider.gameObject;
        //    //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
        //    if (collidingThing.CompareTag("Ship"))
        //    {
        //        Ship ship = collidingThing.GetComponent<Ship>();
        //        ship.LeftNearbyAsteroid(Asteroid);
        //        Asteroid.NearbyShips.Remove(ship);
        //        Debug.Log($"{ship.Name} left {Asteroid.Name}");

        //    }
        //}
    }
}