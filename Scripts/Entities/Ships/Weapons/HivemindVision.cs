using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    // A collider for identifying ships to the Hive Mind
    public class HiveMindVision: MonoBehaviour
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
                        // Clamp the value of seeing the ship between 1 & 20 TSV and add it to the command regardless of what the command is
                        Ship.Squad.GetCommand().Tsv += (int) Mathf.Clamp(_shipEnter.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip, ConfigData.MaximumTsvValueForSeeingAShip);

                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(_shipEnter); // Add the newly seen ship to the hivemind
                        if (Ship.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.Squad.GetCommand()).FoundNewShips(); // If the ship is scouting, note that it's found ships and can end the command
                        }
                    }
                }


            }
            else if (Ship.ShipType == ConfigData.ShipTypes.Beacon && Ship.MotherSquad.HasCommand){
                _shipEnter = collider.GetComponent<Ship>();
                
                if (Ship.IsHiveMindControlled)
                {
                    if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(_shipEnter))
                    {
                        Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {_shipEnter.Name} and added them to hivemind vision");
                        // Clamp the value of seeing the ship between 1 & 20 TSV and add it to the Scout's command regardless of what the command is
                        Ship.MotherSquad.GetCommand().Tsv += (int)Mathf.Clamp(_shipEnter.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip, ConfigData.MaximumTsvValueForSeeingAShip);
                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(_shipEnter);

                        if (Ship.MotherSquad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.MotherSquad.GetCommand()).FoundNewShips();
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