
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

        private Vector2 _size => ConfigData.ShipSizes.GetValueOrDefault(GetFleetShip().Type);
        public FleetShip CachedFleetShip;
        private bool _hasCachedFleetShip = false;


        public SquadShip(long fleetId, ConfigData.ShipTypes shipType, Vector2 offset)
        {
            FleetId = fleetId;
            ShipType = shipType;
            Offset = offset;
        }
        /// <summary>
        /// Grabs and caches the fleet ship. The first time it's called it will minimally need to parse through all the fleet ships to find a matching Id and possibility initialize the fleet data too.
        /// </summary>
        /// <returns></returns>
        public FleetShip GetFleetShip()
        {
            if (!_hasCachedFleetShip)
            {
                //Debug.Log($"Getting fleet ship for {FleetId}, no cached fleetship");
                if (ConfigData.CurrentShips != null)
                {
                    FleetShip fleetShip = ConfigData.CurrentShips.GetFleetShip(FleetId);

                    if (fleetShip != null)
                    {
                        CachedFleetShip = fleetShip;
                    }
                    else
                    {
                        if (FleetId >= 0)
                        {
                            Debug.LogError($"A null fleetship ({ShipType}) was asked for with id #{FleetId}. This was probably a randomly created fleetship.");
                        }
                        CachedFleetShip = new FleetShip(FleetId, ShipType, false, false, 0, 0, 0, 0, 0, 0, 0);
                    }
                }
                else
                {
                    FleetShip fleetShip = ConfigData.GetFleetData().GetFleetShip(FleetId);
                    if (fleetShip != null)
                    {
                        CachedFleetShip = fleetShip;
                    }
                    else
                    {
                        fleetShip = ConfigData.GetCampaignFleetData().GetFleetShip(FleetId);
                        if (fleetShip != null)
                        {
                            CachedFleetShip = fleetShip;
                        }
                        else
                        {
                            fleetShip = ConfigData.GetChallengeFleetData().GetFleetShip(FleetId); // [data-file]
                            if (fleetShip != null)
                            {
                                CachedFleetShip = fleetShip;
                            }
                            else
                            {
                                if (FleetId >= 0)
                                {
                                    Debug.LogError($"A null fleetship ({ShipType}) was asked for with id #{FleetId}. This was probably because the fleet data hasn't loaded yet.");
                                }

                                CachedFleetShip = new FleetShip(FleetId, ShipType, false, false, 0, 0, 0, 0, 0, 0, 0);
                            }

                        }

                    }
                }
            }
            //else
            //{
            //    Debug.Log($"Getting fleet ship for {FleetId}, with cached fleetship");
            //}
            _hasCachedFleetShip = true;
            //Debug.Log($"Returning {_fleetShip} for {this}");
            return CachedFleetShip;

            
        }
        public void SetOffset(Vector2 offset)
        {
            Offset = offset;
        }
        public Vector2 GetOffsetInScreenPixels(Camera camera)
        {
            //Debug.Log($"Getting the offset for {GetFleetShip().Name}. In world units, the offset is {Offset}. In Screen units the offset is {camera.WorldToScreenPoint(Offset)}");
            return camera.WorldToScreenPoint(Offset+ConfigData.StartingPositionOffset);
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
        public override string ToString()
        {
            return $"SquadShip of {ShipType} with FleetId #{FleetId}";
        }
    }
}