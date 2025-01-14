using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
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
        public bool IsTurret;

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
                Weapon.HasCachedChanged = true;

                //if (Weapon.Ship.IsHiveMindControlled && Weapon.Ship.HasCommand)
                //{
                //    //Debug.Log($"{Weapon.Ship.Name} just saw {ship.Name} and added them to hivemind vision");
                //    Level.GetState().HivemindShips[Weapon.Side - 1][Weapon.Ship.Id].Add(ship);
                //    Weapon.Ship.Squad.Command.Tsv += 100;
                //    if (Weapon.Squad.Command.Type == "Scouting")
                //    {
                //        ((Scouting) Weapon.Squad.Command).HasFoundShips = true;
                //    }
                //}
            }

        }
        ///
        protected virtual void OnTriggerExit2D(Collider2D collider) // This is triggered by ships dying too 
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                //Debug.Log($"{ship.Name} is no longer in {Weapon.Ship.Name} range");
                Weapon.ShipsWithinRange.Remove(ship);
                Weapon.HasCachedChanged = true;

                //if (Weapon.Ship.IsHiveMindControlled)
                //{
                //    Level.GetState().HivemindShips[Weapon.Side - 1][Weapon.Ship.Id].Remove(ship);
                //}
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