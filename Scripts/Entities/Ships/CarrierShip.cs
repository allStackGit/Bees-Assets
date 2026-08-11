


using Assets.Scripts.Data;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class CarrierShip : Ship
    {

        public Carrier Carrier;

        public override void ClearData()
        {
            base.ClearData();
            // Drone/Striker pools serve both Carrier children and ordinary ships. A pooled
            // wrapper must not keep the Carrier from its previous lifecycle; real Carrier
            // children receive their new owner later through CarrierShipSetup().
            Carrier = null;
        }

        public void CarrierShipSetup(FleetShip fleetShip, ConfigData.ShipTypes shipType, Carrier carrier)
        {
            FleetShip = fleetShip;
            ShipType = shipType;
            Carrier = carrier;
        }
    }
}