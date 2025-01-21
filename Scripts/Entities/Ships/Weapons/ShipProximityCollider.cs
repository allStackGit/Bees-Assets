using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class ShipProximityCollider : MonoBehaviour
    {
        public Ship Ship;
        public int Range;
        public CircleCollider2D Collider;
        public HashSet<Ship> NearbyEnemyShips = new HashSet<Ship>();

        public virtual void Setup(Ship ship, int range)
        {
            Ship = ship;
            Range = range;
            Collider.radius = Range;
        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship.Side != Ship.Side)
                {
                    NearbyEnemyShips.Add(ship);
                }
                //Debug.Log($"{ship.Name} is nearby {Ship.Name}");
            }

        }
        protected virtual void OnTriggerExit2D(Collider2D collider) // This is triggered by ships dying too 
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                NearbyEnemyShips.Remove(ship);
                //Debug.Log($"{ship.Name} is no longer nearby {Ship.Name}");

            }
        }
    }
}