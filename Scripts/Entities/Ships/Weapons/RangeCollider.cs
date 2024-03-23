using Assets.Scripts.Entities.Projectiles;
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

        public virtual void Setup(Weapon weapon, int range)
        {
            Weapon = weapon;
            Range = range;
            Weapon.HasRangeCollider = true;
            Collider.radius = Range;

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                Weapon.ShipsWithinRange.Add(ship);
            }else if (collidingThing.CompareTag("Fog of War") && Weapon.Ship.IsUserControlled){
                Destroy(collidingThing);
            }

        }
        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                Weapon.ShipsWithinRange.Remove(ship);
            }
            else if (collidingThing.CompareTag("Projectile"))
            {
                Projectile projectile = collidingThing.GetComponent<Projectile>();
                if (projectile.Weapon.Equals(Weapon))
                {
                    //Debug.Log($"{Weapon.Ship.Name}'s projectile left it's range!");
                    projectile.Kill();
                }
            }
        }
    }
}