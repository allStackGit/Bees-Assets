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
    public class HivemindVision: MonoBehaviour
    {

        public CircleCollider2D Collider;
        public Ship Ship;
        public int Range;

        public void Create(Ship ship)
        {
            Ship = ship;
            int range = Ship.Sight;
            if (range == 0)
            {
                range = Ship.MaxRange;
            }
            Collider.radius = range;
        }
        public void Activate()
        {

            Collider.enabled = true;
            enabled = true;
        }
        private Ship _shipEnter;
        protected void OnTriggerEnter2D(Collider2D collider)
        {
            if (Ship.Squad.HasCommand)
            {
                _shipEnter = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (Ship.IsHiveMindControlled && !Ship.IsDead)
                {
                    if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(_shipEnter))
                    {
                        Ship.Squad.GetCommand().Tsv += (int)Math.Min(Math.Max(_shipEnter.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(_shipEnter);
                        if (Ship.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.Squad.GetCommand()).FoundShips();
                        }
                    }
                }


            }
            else if (Ship.ShipType == ConfigData.ShipTypes.Beacon && Ship.MotherSquad.HasCommand){
                _shipEnter = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (Ship.IsHiveMindControlled)
                {
                    if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(_shipEnter))
                    {
                        Ship.MotherSquad.GetCommand().Tsv += (int)Math.Min(Math.Max(_shipEnter.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(_shipEnter);

                        if (Ship.MotherSquad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.MotherSquad.GetCommand()).FoundShips();
                        }
                    }
                }

            }
            //else
            //{
            //    Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw a ship but did not add them to hivemind vision because there is no squad command");
            //}

        }


        public void Deactivate()
        {
            Collider.enabled = false;
            enabled = false;
        }
    }
}