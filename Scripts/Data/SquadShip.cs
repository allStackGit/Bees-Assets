
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
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
            if (ConfigData.CurrentShips != null)
            {
                FleetShip fleetShip = ConfigData.CurrentShips.GetFleetShip(FleetId);
                if (fleetShip != null)
                {
                    return fleetShip;
                }
                else
                {
                    //Debug.LogWarning($"A null fleetship was asked for with id #{FleetId}. This was probably a randomly created fleetship.");
                    return new FleetShip(FleetId, $"{ShipType} - #{FleetId}", ShipType, false, true, false, 0, 0, 0, 0, 0, 0, 0);
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
                    return new FleetShip(FleetId, $"{ShipType} - #{FleetId}", ShipType, false, true, false, 0, 0, 0, 0, 0, 0, 0);
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
        public Ship ToShip(Level level, Squad squad)
        {
            FleetShip fleetShip = GetFleetShip();
            Ship ship = null;
            switch (fleetShip.Type)
            {
                case "Barge":
                    ship = level.gameObject.AddComponent<Barge>();
                    break;
                case "Beehive":
                    ship = level.gameObject.AddComponent<Beehive>();
                    break;
                case "Bumblebee":
                    ship = level.gameObject.AddComponent<Bumblebee>();
                    break;
                case "Carpenter Bee":
                    ship = level.gameObject.AddComponent<CarpenterBee>();
                    break;
                case "Carrier":
                    ship = level.gameObject.AddComponent<Carrier>();
                    break;
                case "Cruiser":
                    ship = level.gameObject.AddComponent<Cruiser>();
                    break;
                case "Dreadnought":
                    ship = level.gameObject.AddComponent<Dreadnought>();
                    break;
                case "Drone":
                    ship = level.gameObject.AddComponent<Drone>();
                    break;
                case "Factory":
                    ship = level.gameObject.AddComponent<Factory>();
                    break;
                case "Fire Barge":
                    ship = level.gameObject.AddComponent<FireBarge>();
                    break;
                case "Flagship":
                    ship = level.gameObject.AddComponent<Flagship>();
                    break;
                case "Frigate":
                    ship = level.gameObject.AddComponent<Frigate>();
                    break;
                case "Gunship":
                    ship = level.gameObject.AddComponent<Gunship>();
                    break;
                case "Honeybee":
                    ship = level.gameObject.AddComponent<Honeybee>();
                    break;
                case "Hornet":
                    ship = level.gameObject.AddComponent<Hornet>();
                    break;
                case "Leafcutter":
                    ship = level.gameObject.AddComponent<Leafcutter>();
                    break;
                case "Queen":
                    ship = level.gameObject.AddComponent<Queen>();
                    break;
                case "Scout":
                    ship = level.gameObject.AddComponent<Scout>();
                    break;
                case "Striker":
                    ship = level.gameObject.AddComponent<Striker>();
                    break;
                case "Warp Gate":
                    ship = level.gameObject.AddComponent<WarpGate>();
                    break;
                case "Wasp":
                    ship = level.gameObject.AddComponent<Wasp>();
                    break;
                case "Yellow Jacket":
                    ship = level.gameObject.AddComponent<YellowJacket>();
                    break;
            }
            if (ship != null)
            {
                ship.Setup(
                    level,
                    level.GetState().GetId(),
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