
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
        public long FleetId;
        public ConfigData.ShipTypes ShipType;
        public Vector2 Offset;

        private SavedSquad _squad;
        private Vector2 _size => ConfigData.ShipSizes.GetValueOrDefault(GetFleetShip().Type);


        public SquadShip(long fleetId, ConfigData.ShipTypes shipType, Vector2 offset, SavedSquad squad)
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
        public string ToJson()
        {
            return $"{{\"FleetId\": {FleetId}, \"ShipType\": \"{Utilities.ConvertShipTypeToName[ShipType]}\", \"Offset\": {{ \"x\": {Offset.x}, \"y\": {Offset.y} }}}}";
        }
        public bool Equals (SquadShip other)
        {
            return FleetId == other.FleetId;
        }
    }
}