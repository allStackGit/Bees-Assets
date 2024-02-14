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
            SavedSquad = Carrier.Squad.SavedSquad;
            SquadType = squadType;
            SetupShips();
            SetShootingStrategy(carrier.Squad.GetShootingStrategy());
        }
        private void SetupShips()
        {
            int shipCount = IsDroneSquad ? ConfigData.Configuration.CarrierCarryDroneMax : ConfigData.Configuration.CarrierCarryStrikerMax;
            string formation = "Column";

            // If there are more than 10 drones, a double column might be the best formation, but currently we are using 10 drones and 10 strikers, one column each
            //    IsDroneSquad ? "Double Column" : "Column";

            //Debugger.Log($"Setting up {SquadType} ships in {formation} formation");


            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                int id = (int) Utilities.Hash() + ConfigData.AllShips.GetFleetShips().Count;
                Vector2 offset = Vector2.left;

                if (formation == "Double Column")
                {
                    offset = ConfigData.CarrierDoubleColumnFormationOffsets[shipIndex];
                }else if (formation == "Column")
                {
                    offset = ConfigData.CarrierColumnFormationOffsets[shipIndex];
                }

                //Debugger.Log($"Offset: {offset}");
                CarrierShip ship;
                (GameObject, CarrierShip) tuple = ((GameObject, CarrierShip))Level.LevelConstructor.InstantiateShip(SquadType);
                ship = tuple.Item2;


                if (ship != null)
                {
                    ship.Setup(
                        Level,
                        Level.GetState().EntityCount++,
                        new FleetShip(id, Side, $"Carrier {SquadType} - #{id}", SquadType, true, false, 0, 0, 0, 0, 0, 0),
                        this,
                        offset
                    );
                }
                AddShip(ship);
                Carrier.AdditionalTsv += ship.Tsv;
                Carrier.OriginalTsv = Carrier.Tsv;
                ship.SetColor();
            }

        }
    }

}