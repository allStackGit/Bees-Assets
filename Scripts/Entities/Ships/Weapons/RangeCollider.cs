using System.Collections;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class RangeCollider : MonoBehaviour
    {
        public Weapon Weapon;
        public int Range;
        public CircleCollider2D Collider;

        public virtual void Setup(Weapon weapon, int range, Transform piece)
        {
            Weapon = weapon;
            Range = range;
            Weapon.HasRangeCollider = true;
            Collider.radius = Range;

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            Transform collidingThing = collider.transform;
            if (collidingThing != null)
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship != null)
                {
                    Weapon.ShipsWithinRange.Add(ship);
                    //Debug.Log($"{Weapon.Name} has detected {ship.Name} within range");
                }
            }

        }
        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            Transform collidingThing = collider.transform;
            if (collidingThing != null)
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship != null)
                {
                    Weapon.ShipsWithinRange.Remove(ship);
                    //Debug.Log($"{Weapon.Name} has detected {ship.Name} leaving range");
                }
            }
        }
    }
}