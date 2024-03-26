using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
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
                Collider.radius = ship.Sight;
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
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Fog of War"))
            {
                Destroy(collidingThing);
            }
            else if (Ship.IsHiveMindControlled && collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (Ship.Squad.HasCommand)
                {
                    //Debug.Log($"{Ship.Name} just saw {ship.Name} and added them to hivemind vision");
                    Ship.Squad.Command.Tsv += 100;
                    Ship.Level.GetState().HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);
                    if (Ship.Squad.Command.Type == "Scouting")
                    {
                        ((Scouting)Ship.Squad.Command).HasFoundShips = true;
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