


using Assets.Scripts.Data;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class CarrierShip : Ship
    {

        public Carrier Carrier;
        public void CarrierShipSetup(FleetShip fleetShip, ConfigData.ShipTypes shipType, Carrier carrier)
        {
            FleetShip = fleetShip;
            ShipType = shipType;
            Carrier = carrier;
        }
    }
}