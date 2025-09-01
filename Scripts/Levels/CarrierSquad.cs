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
        private int _shipCount, _shipIndex;
        private long _id;
        private CarrierShip _ship;
        private void SetupShips()
        {
            _shipCount = IsDroneSquad ? ConfigData.Configuration.CarrierCarryDroneMax : ConfigData.Configuration.CarrierCarryStrikerMax;

            //Debug.Log($"Setting up {SquadType} ships in {formation} formation");


            for (_shipIndex = 0; _shipIndex < _shipCount; _shipIndex++)
            {
                _id = Utilities.GetNegativeFleetshipId();

                //Debug.Log($"Offset: {offset}");
                _ship = (CarrierShip)Level.LevelConstructor.InstantiateShip(CarrierSquadType);

                _ship.Setup(
                        Level,
                        new FleetShip(_id, CarrierSquadType, true, true, false, 0, 0, 0, 0, 0, 0, 0),
                        this,
                        ConfigData.CarrierColumnFormationOffsets[_shipIndex]
                    );
                _ship.IsCarrierShip = true;
                AddShip(_ship);
                _ship.SetColor();
            }

        }
    }

}