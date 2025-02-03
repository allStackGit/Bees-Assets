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
            Stage = stage;
            SquadType = ConfigData.SquadTypes.CarrierSquad;
        }
        public void SetupCarrierSquad(Carrier carrier, ConfigData.ShipTypes squadType)
        {
            Carrier = carrier;
            CarrierSquadType = squadType;
            IsCarrierSquad = true;
            SetupShips();
            SetShootingStrategy(carrier.Squad.GetShootingStrategy());
            SetSquadTab();
            IsDroneSquad = CarrierSquadType == ConfigData.ShipTypes.Drone;
        }
        private void SetupShips()
        {
            int shipCount = IsDroneSquad ? ConfigData.Configuration.CarrierCarryDroneMax : ConfigData.Configuration.CarrierCarryStrikerMax;
            string formation = "Column";

            //Debug.Log($"Setting up {SquadType} ships in {formation} formation");


            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                int id = Utilities.GetNegativeFleetshipId();
                Vector2 offset = Vector2.left;

                if (formation == "Double Column")
                {
                    offset = ConfigData.CarrierDoubleColumnFormationOffsets[shipIndex];
                }else if (formation == "Column")
                {
                    offset = ConfigData.CarrierColumnFormationOffsets[shipIndex];
                }

                //Debug.Log($"Offset: {offset}");
                CarrierShip ship = (CarrierShip)Level.LevelConstructor.InstantiateShip(CarrierSquadType);

                if (ship != null)
                {
                    ship.Setup(
                        Level,
                        new FleetShip(id, $"Carrier {CarrierSquadType} - #{id}", CarrierSquadType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
                        this,
                        offset
                    );
                }
                ship.IsCarrierShip = true;
                AddShip(ship);
                ship.SetColor();
            }

        }
    }

}