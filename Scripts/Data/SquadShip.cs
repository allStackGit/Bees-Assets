
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class SquadShip : ICloneable
    {
        public int FleetId;
        public string ShipType;
        public Vector2 Offset;

        private SavedSquad _squad;
        private Vector2 _size => ConfigData.ShipSizes.GetValueOrDefault(GetFleetShip().Type);


        public SquadShip(int fleetId, string shipType, Vector2 offset, SavedSquad squad)
        {
            FleetId = fleetId;
            ShipType = shipType;
            Offset = offset;
            _squad = squad;
        }
        public FleetShip GetFleetShip()
        {
            if (ConfigData.AllShips != null)
            {
                FleetShip fleetShip = ConfigData.AllShips.GetFleetShip(FleetId);
                if (fleetShip != null)
                {
                    return fleetShip;
                }
                else
                {
                    //Debug.LogWarning($"A null fleetship was asked for with id #{FleetId}. This was probably a randomly created fleetship.");
                    return new FleetShip(FleetId, _squad.Side, $"{ShipType} - #{FleetId}", ShipType, true, false, 0, 0, 0, 0, 0, 0);
                }
            }
            else
            {
                FleetShip fleetShip = ConfigData.GetFleetData().GetFleetShip(FleetId);
                if (fleetShip != null)
                {
                    return fleetShip;
                }
                else
                {
                    //Debug.LogWarning($"A null fleetship was asked for with id #{FleetId}. This was probably a randomly created fleetship.");
                    return new FleetShip(FleetId, _squad.Side, $"{ShipType} - #{FleetId}", ShipType, true, false, 0, 0, 0, 0, 0, 0);
                }
            }
            
        }
        public void SetOffset(Vector2 offset)
        {
            Offset = offset;
        }
        public Vector2 GetOffsetInScreenPixels(Camera camera)
        {
            //Debug.Log($"Getting the offset for {GetFleetShip().Name}. In world units, the offset is {Offset}. In Screen units the offset is {camera.WorldToScreenPoint(Offset)}");
            return camera.WorldToScreenPoint(Offset);
            //return Utilities.WorldUnitsToScreenPixels(Offset, camera);
        }
        public Vector2 GetTopSide()
        {
            return new Vector2(Offset.x, Offset.y + _size.y/2);
        }
        public Vector2 GetLeftSide()
        {
            return new Vector2(Offset.x - _size.x/2, Offset.y);
        }
        public Vector2 GetRightSide()
        {
            return new Vector2(Offset.x + _size.x/2, Offset.y);
        }
        public Vector2 GetBottomSide()
        {
            return new Vector2(Offset.x, Offset.y - _size.y/2);
        }
        public object Clone()
        {
            SquadShip clone = (SquadShip)this.MemberwiseClone();
            return clone;
        }
        public Ship ToShip(LevelStage level, Squad squad)
        {
            FleetShip fleetShip = GetFleetShip();
            Ship ship = null;
            switch (fleetShip.Type)
            {
                case "Barge":
                    ship = level.AddComponent<Barge>();
                    break;
                case "Beehive":
                    ship = level.AddComponent<Beehive>();
                    break;
                case "Bumblebee":
                    ship = level.AddComponent<Bumblebee>();
                    break;
                case "Carpenter Bee":
                    ship = level.AddComponent<CarpenterBee>();
                    break;
                case "Carrier":
                    ship = level.AddComponent<Carrier>();
                    break;
                case "Cruiser":
                    ship = level.AddComponent<Cruiser>();
                    break;
                case "Dreadnought":
                    ship = level.AddComponent<Dreadnought>();
                    break;
                case "Drone":
                    ship = level.AddComponent<Drone>();
                    break;
                case "Factory":
                    ship = level.AddComponent<Factory>();
                    break;
                case "Fire Ship":
                    ship = level.AddComponent<FireShip>();
                    break;
                case "Flagship":
                    ship = level.AddComponent<Flagship>();
                    break;
                case "Frigate":
                    ship = level.AddComponent<Frigate>();
                    break;
                case "Gunship":
                    ship = level.AddComponent<Gunship>();
                    break;
                case "Honeybee":
                    ship = level.AddComponent<Honeybee>();
                    break;
                case "Hornet":
                    ship = level.AddComponent<Hornet>();
                    break;
                case "Leafcutter":
                    ship = level.AddComponent<Leafcutter>();
                    break;
                case "Queen":
                    ship = level.AddComponent<Queen>();
                    break;
                case "Scout":
                    ship = level.AddComponent<Scout>();
                    break;
                case "Striker":
                    ship = level.AddComponent<Striker>();
                    break;
                case "Warp Gate":
                    ship = level.AddComponent<WarpGate>();
                    break;
                case "Wasp":
                    ship = level.AddComponent<Wasp>();
                    break;
                case "Yellow Jacket":
                    ship = level.AddComponent<YellowJacket>();
                    break;
            }
            if (ship != null)
            {
                ship.Setup(
                    level,
                    level.GetState().EntityCount++,
                    fleetShip,
                    squad,
                    Offset
                );
            }

            return ship;
        }
        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
        public bool Equals (SquadShip other)
        {
            return FleetId == other.FleetId;
        }
    }
}