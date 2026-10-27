using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class CarrierSquad : Squad
    {
        public ConfigData.ShipTypes CarrierSquadType;
        public Carrier Carrier;
        public bool IsDroneSquad;
        public override void Create(Stage stage)
        {
            base.Create(stage);
            SquadType = ConfigData.SquadTypes.CarrierSquad;
            IsMinionSquad = true;
        }
        public override void ClearData()
        {
            base.ClearData();
            Carrier = null;
            CarrierSquadType = default;
            IsDroneSquad = false;
            IsCarrierSquad = true;
            IsMinionSquad = true;
        }
        public void SetupCarrierSquad(Carrier carrier, ConfigData.ShipTypes squadType)
        {
            Carrier = carrier;
            CarrierSquadType = squadType;
            IsCarrierSquad = true;
            IsMinionSquad = true;
            IsDroneSquad = squadType == ConfigData.ShipTypes.Drone;
        }

        // Called in alternating Drone/Striker order by the LevelConstructor. Keeping each
        // ship in its own type-specific squad preserves Carrier commands and formations.
        public void AddInitialCarrierShip()
        {
            int shipIndex = GetShips().Count;
            int naturalMaximum = IsDroneSquad
                ? ConfigData.Configuration.CarrierCarryDroneMax
                : ConfigData.Configuration.CarrierCarryStrikerMax;
            if (shipIndex >= naturalMaximum)
            {
                throw new System.InvalidOperationException("Carrier squad exceeded its natural ship capacity.");
            }

            long id = TransientIdAllocator.GetFleetShipId();
            CarrierShip ship = (CarrierShip)Level.LevelConstructor.InstantiateShip(CarrierSquadType);
            ship.IsCarrierShip = true;
            ship.Setup(
                Level,
                new FleetShip(id, CarrierSquadType, true, false, 0, 0, 0, 0, 0, 0, 0),
                this,
                ConfigData.CarrierColumnFormationOffsets[shipIndex]);
            AddShip(ship);
            ship.SetColor();
        }

        public void CompleteInitialCarrierSquad()
        {
            SetShootingStrategy(Carrier.Squad.GetShootingStrategy());
            SetSquadTab();
        }
    }
}
