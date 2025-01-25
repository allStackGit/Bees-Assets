
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
        public ConfigData.ShipTypes ShipType;
        public Vector2 Offset;

        private SavedSquad _squad;
        private Vector2 _size => ConfigData.ShipSizes.GetValueOrDefault(GetFleetShip().Type);


        public SquadShip(int fleetId, ConfigData.ShipTypes shipType, Vector2 offset, SavedSquad squad)
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
                case ConfigData.ShipTypes.Barge:
                    ship = level.gameObject.AddComponent<Barge>();
                    break;
                case ConfigData.ShipTypes.Beehive:
                    ship = level.gameObject.AddComponent<Beehive>();
                    break;
                case ConfigData.ShipTypes.Bumblebee:
                    ship = level.gameObject.AddComponent<Bumblebee>();
                    break;
                case ConfigData.ShipTypes.CarpenterBee:
                    ship = level.gameObject.AddComponent<CarpenterBee>();
                    break;
                case ConfigData.ShipTypes.Carrier:
                    ship = level.gameObject.AddComponent<Carrier>();
                    break;
                case ConfigData.ShipTypes.Cruiser:
                    ship = level.gameObject.AddComponent<Cruiser>();
                    break;
                case ConfigData.ShipTypes.Dreadnought:
                    ship = level.gameObject.AddComponent<Dreadnought>();
                    break;
                case ConfigData.ShipTypes.Drone:
                    ship = level.gameObject.AddComponent<Drone>();
                    break;
                case ConfigData.ShipTypes.Factory:
                    ship = level.gameObject.AddComponent<Factory>();
                    break;
                case ConfigData.ShipTypes.FireBarge:
                    ship = level.gameObject.AddComponent<FireBarge>();
                    break;
                case ConfigData.ShipTypes.Flagship:
                    ship = level.gameObject.AddComponent<Flagship>();
                    break;
                case ConfigData.ShipTypes.Frigate:
                    ship = level.gameObject.AddComponent<Frigate>();
                    break;
                case ConfigData.ShipTypes.Gunship:
                    ship = level.gameObject.AddComponent<Gunship>();
                    break;
                case ConfigData.ShipTypes.Honeybee:
                    ship = level.gameObject.AddComponent<Honeybee>();
                    break;
                case ConfigData.ShipTypes.Hornet:
                    ship = level.gameObject.AddComponent<Hornet>();
                    break;
                case ConfigData.ShipTypes.Leafcutter:
                    ship = level.gameObject.AddComponent<Leafcutter>();
                    break;
                case ConfigData.ShipTypes.Queen:
                    ship = level.gameObject.AddComponent<Queen>();
                    break;
                case ConfigData.ShipTypes.Scout:
                    ship = level.gameObject.AddComponent<Scout>();
                    break;
                case ConfigData.ShipTypes.Striker:
                    ship = level.gameObject.AddComponent<Striker>();
                    break;
                case ConfigData.ShipTypes.WarpGate:
                    ship = level.gameObject.AddComponent<WarpGate>();
                    break;
                case ConfigData.ShipTypes.Wasp:
                    ship = level.gameObject.AddComponent<Wasp>();
                    break;
                case ConfigData.ShipTypes.YellowJacket:
                    ship = level.gameObject.AddComponent<YellowJacket>();
                    break;
            }
            if (ship != null)
            {
                ship.Setup(
                    level,
                    level.State.GetId(),
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