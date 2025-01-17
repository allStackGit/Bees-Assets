using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level.Commands;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Level
{
    public class CarrierSquad : Squad
    {
        public string SquadType;
        public Carrier Carrier;
        public bool IsDroneSquad => SquadType == "Drone" ? true : false;
        public bool IsStrikerSquad => SquadType == "Striker" ? true: false;
        public void SetupCarrierSquad(Carrier carrier, string squadType)
        {
            Carrier = carrier;
            SquadType = squadType;
            SetupShips();
            SetShootingStrategy(carrier.Squad.GetShootingStrategy());
            SetSquadTab();
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
                CarrierShip ship;
                (GameObject, CarrierShip) tuple = ((GameObject, CarrierShip))Level.LevelConstructor.InstantiateShip(SquadType);
                ship = tuple.Item2;


                if (ship != null)
                {
                    ship.Setup(
                        Level,
                        Level.GetState().GetId(),
                        new FleetShip(id, Side, $"Carrier {SquadType} - #{id}", SquadType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
                        this,
                        offset
                    );
                }
                AddShip(ship);
                ship.SetColor();
            }

        }
    }

}