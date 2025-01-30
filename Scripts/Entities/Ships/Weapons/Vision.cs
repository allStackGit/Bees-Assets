using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
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
                int range = Ship.Sight;
                if (range == 0)
                {
                    range = Ship.MaxRange;
                }
                Collider.radius = range;
                Collider.isTrigger = true;
            }
            else
            {
                int range = Ship.Sight * 2;
                if (range == 0)
                {
                    range = Ship.MaxRange * 2;
                }
                FogIlluminator = gameObject.AddComponent<SpriteMask>();
                FogIlluminator.sprite = Ship.Stage.VisonSprite;
                FogIlluminator.alphaCutoff = .5f;
                transform.localScale = new Vector3(range, range, 0);
                gameObject.layer = ConfigData.FogOfWarLayer;

            }
            //Debug.Log($"{Ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {Ship.Sight}");

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            if (Ship.Squad.HasCommand)
            {
                Ship ship = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(ship))
                {
                    Ship.Squad.Command.Tsv += (int)Math.Min(Math.Max(ship.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                    Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);
                    if (Ship.Squad.Command.CommandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)Ship.Squad.Command).FoundShips();
                    }
                }

            }else if (Ship.ShipType == ConfigData.ShipTypes.Beacon && Ship.MotherSquad.HasCommand){
                Ship ship = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(ship))
                {
                    Ship.MotherSquad.Command.Tsv += (int)Math.Min(Math.Max(ship.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                    Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);
                    if (Ship.MotherSquad.Command.CommandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)Ship.MotherSquad.Command).FoundShips();
                    }
                }
            }

        }

        public void Kill(float initialDelay)
        {
            transform.SetParent(Ship.Level.Map.transform);
            InvokeRepeating(nameof(ShrinkVision), initialDelay, .1f);
        }

        public void ShrinkVision()
        {
            transform.localScale *= ConfigData.VisionShrinkingMultiplier;
            if (transform.localScale.x < 3)
            {
                //Destroy(gameObject);
                CancelInvoke(nameof(ShrinkVision));
                gameObject.SetActive(false);
            }
        }

        //protected virtual void OnTriggerExit2D(Collider2D collider)
        //{
        //    GameObject collidingThing = collider.gameObject;
        //    if (Ship.IsHiveMindControlled && collidingThing.CompareTag("Ship"))
        //    {
        //        Ship ship = collidingThing.GetComponent<Ship>();
        //        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Remove(ship);
        //    }

        //}
    }
}