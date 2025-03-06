using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ProximityCollider : MonoBehaviour
    {
        public CircleCollider2D Collider;
        public HashSet<Ship> NearbyEnemyShips = new HashSet<Ship>();
        public Ship Ship;
        
        public void Create(Ship ship)
        {
            Ship = ship;
            int proximityRange = Ship.Sight;
            if (proximityRange == 0)
            {
                proximityRange = Ship.MaxRange;
            }
            Collider.radius = proximityRange;
            Collider.isTrigger = true;
        }

        public void Activate()
        {
            NearbyEnemyShips.Clear();

            Collider.enabled = true;
            enabled = true;
        }
        public void Deactivate()
        {
            Collider.enabled = false;
            enabled = false;
        }

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            NearbyEnemyShips.Add(collider.GetComponent<Ship>());
            Debug.Log($"Just added {collider.GetComponent<Ship>()} to {Ship} NearbyShips");

        }
        protected void OnTriggerExit2D(Collider2D collider)
        {
            NearbyEnemyShips.Remove(collider.GetComponent<Ship>());
        }
    }
}