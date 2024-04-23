using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    // A collider for clearinng fog of war for ships that don't have range colliders
    public class Vision : MonoBehaviour
    {

        public CircleCollider2D Collider;
        public SpriteMask FogIlluminator;
        public Ship Ship;

        public virtual void Setup(Ship ship)
        {
            //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");
            Ship = ship;
            if (Ship.IsHiveMindControlled)
            {
                Collider = gameObject.AddComponent<CircleCollider2D>();
                int range = ship.Sight;
                if (range == 0)
                {
                    range = ship.MaxRange;
                }
                Collider.radius = range;
                Collider.isTrigger = true;
            }
            else
            {
                int range = ship.Sight * 2;
                if (range == 0)
                {
                    range = ship.MaxRange * 2;
                }
                FogIlluminator = gameObject.AddComponent<SpriteMask>();
                FogIlluminator.sprite = Ship.Level.VisonSprite;
                FogIlluminator.alphaCutoff = .5f;
                transform.localScale = new Vector3(range, range, 0);
                gameObject.layer = ConfigData.FogOfWarLayer;

            }
            //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            if (Ship.Squad.HasCommand)
            {
                Ship ship = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (!Ship.Level.GetState().VisionCache[Ship.Side - 1].Contains(ship))
                {
                    Ship.Squad.Command.Tsv += (int)Math.Min(Math.Max(ship.Tsv * .05f, 50), 500);
                    Ship.Level.GetState().HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);
                    if (Ship.Squad.Command.Type == "Scouting")
                    {
                        ((Scouting)Ship.Squad.Command).FoundShips();
                    }
                }

            }

        }

        //protected virtual void OnTriggerExit2D(Collider2D collider)
        //{
        //    GameObject collidingThing = collider.gameObject;
        //    if (Ship.IsHiveMindControlled && collidingThing.CompareTag("Ship"))
        //    {
        //        Ship ship = collidingThing.GetComponent<Ship>();
        //        Ship.Level.GetState().HivemindShips[Ship.Side - 1][Ship.Id].Remove(ship);
        //    }

        //}
    }
}