using Assets.Scripts.Entities.Ships;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Zone : MonoBehaviour
    {
        public HashSet<Ship> Ships = new HashSet<Ship>();
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