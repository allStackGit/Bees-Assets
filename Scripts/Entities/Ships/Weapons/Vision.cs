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
        public int Range;

        public void Create(Ship ship)
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
                Range = Ship.Sight * 2;
                if (Range == 0)
                {
                    Range = Ship.MaxRange * 2;
                }
                FogIlluminator = gameObject.AddComponent<SpriteMask>();
                FogIlluminator.sprite = Ship.Stage.VisonSprite;
                FogIlluminator.alphaCutoff = .5f;
                gameObject.layer = ConfigData.FogOfWarLayer;

            }
            NearbyEnemyShips.Clear();
        }
        public void Activate()
        {
            
            NearbyEnemyShips.Clear();

            if (Ship.IsUserControlled)
            {
                transform.SetParent(Ship.transform);
                transform.localScale = new Vector3(Range, Range, 0);
            }
            if (Ship.IsHiveMindControlled || Ship.HasProximityCollider)
            {
                Collider.enabled = true;

                //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");
            }
            //Debug.Log($"{Ship.Name} : {gameObject.name`} Collider.radius: {Collider.radius}, Sight: {Ship.Sight}");
            enabled = true;
        }
        private Ship _shipEnter;
        protected void OnTriggerEnter2D(Collider2D collider)
        {
            if (Ship.Squad.HasCommand)
            {
                _shipEnter = collider.GetComponent<Ship>();
                //Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw {ship.Name} and added them to hivemind vision");
                if (Ship.IsHiveMindControlled)
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
                NearbyEnemyShips.Add(_shipEnter);


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
                NearbyEnemyShips.Add(_shipEnter);

            }
            //else
            //{
            //    Debug.Log($"{Ship.Name} from {Ship.Level.gameObject.name} just saw a ship but did not add them to hivemind vision because there is no squad command");
            //}

        }
        private ScaledTimer _shrinkVisionStartTimer = new ScaledTimer();
        private ScaledTimer _shrinkVisionTimer = new ScaledTimer();
        public void Kill(float initialDelay)
        {
            transform.SetParent(Ship.Level.Map.transform);
            _shrinkVisionTimer.Reuse(1f, ShrinkVision, true);
            _shrinkVisionStartTimer.Reuse(initialDelay, () =>
            {
                Ship.Level.AddTimer(_shrinkVisionTimer);
            });
            Ship.Level.AddTimer(_shrinkVisionStartTimer);
            //InvokeRepeating(nameof(ShrinkVision), initialDelay, .1f);
        }

        public void ShrinkVision()
        {
            transform.localScale *= ConfigData.VisionShrinkingMultiplier;
            if (transform.localScale.x < 3)
            {
                Ship.Level.CancelTimer(_shrinkVisionTimer);
                //CancelInvoke(nameof(ShrinkVision));
                Deactivate();
            }
        }
        protected void OnTriggerExit2D(Collider2D collider)
        {
            NearbyEnemyShips.Remove(collider.GetComponent<Ship>());
        }

        public void Deactivate()
        {
            if (Ship.IsHiveMindControlled || Ship.HasProximityCollider)
            {
                Collider.enabled = false;
            }
            enabled = false;
        }
    }
}