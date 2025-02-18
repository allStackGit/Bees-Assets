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
        public HashSet<Ship> NearbyEnemyShips = new HashSet<Ship>();


        public virtual void Setup(Ship ship)
        {
            Ship = ship;
            if (Ship.IsHiveMindControlled || Ship.HasProximityCollider)
            {
                Collider = gameObject.AddComponent<CircleCollider2D>();
                int range = Ship.Sight;
                if (range == 0)
                {
                    range = Ship.MaxRange;
                }
                Collider.radius = range;
                Collider.isTrigger = true;

                //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");
            }
            if (Ship.IsUserControlled)
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
            NearbyEnemyShips.Clear();
            //Debug.Log($"{Ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {Ship.Sight}");

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            if (Ship.Squad.HasCommand)
            {
                Ship ship = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (Ship.IsHiveMindControlled)
                {
                    if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(ship))
                    {
                        Ship.Squad.GetCommand().Tsv += (int)Math.Min(Math.Max(ship.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);
                        if (Ship.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.Squad.GetCommand()).FoundShips();
                        }
                    }
                }
                NearbyEnemyShips.Add(ship);


            }else if (Ship.ShipType == ConfigData.ShipTypes.Beacon && Ship.MotherSquad.HasCommand){
                Ship ship = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (Ship.IsHiveMindControlled)
                {
                    if (!Ship.Level.State.VisionCache[Ship.Side - 1].Contains(ship))
                    {
                        Ship.MotherSquad.GetCommand().Tsv += (int)Math.Min(Math.Max(ship.Tsv * ConfigData.TsvMultiplierForVision, ConfigData.MinimumTsvValueForSeeingAShip), ConfigData.MaximumTsvValueForSeeingAShip);
                        Ship.Level.State.HivemindShips[Ship.Side - 1][Ship.Id].Add(ship);

                        if (Ship.MotherSquad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
                        {
                            ((Scouting)Ship.MotherSquad.GetCommand()).FoundShips();
                        }
                    }
                }
                NearbyEnemyShips.Add(ship);

            }
            //else
            //{
            //    Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw a ship but did not add them to hivemind vision because there is no squad command");
            //}

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
                CancelInvoke(nameof(ShrinkVision));
                gameObject.SetActive(false);
            }
        }

        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            Ship ship = collider.GetComponent<Ship>();
            NearbyEnemyShips.Remove(ship);
        }
    }
}