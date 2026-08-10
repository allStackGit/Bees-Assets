using Assets.Scripts.Entities.Ships;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Zone : MonoBehaviour
    {
        public HashSet<Ship> Ships = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public Action<Ship> OnShipEnter;


        private void OnTriggerEnter2D(Collider2D collision)
        {
            Ship ship = collision.GetComponent<Ship>();
            if (ship == null || ship.IsDead)
            {
                return;
            }

            if (Ships.Add(ship))
            {
                //Debug.Log($"Ship {ship.Name} entered exit zone.");
                OnShipEnter?.Invoke(ship);

                // Extraction/retreat callbacks commonly EndKill the entering ship. Unity
                // does not guarantee a matching trigger-exit callback when a collider is
                // disabled during this physics callback, so do not retain a consumed pooled
                // wrapper in the zone until a future lifecycle reuses it.
                if (ship == null || ship.IsDead || !ship.gameObject.activeInHierarchy)
                {
                    Ships.Remove(ship);
                }
            }
        }

        private void OnTriggerExit2D(Collider2D collision)
        {
            Ship ship = collision.GetComponent<Ship>();
            if (ship != null)
            {
                Ships.Remove(ship);
                //Debug.Log($"Ship {ship.Name} exited exit zone.");
            }
        }
    }
}
