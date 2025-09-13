using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Scenes;
using UnityEngine;

namespace Assets.Scripts.Data
{
    /// <summary>
    /// class that holds and manages storage for user fleet data
    /// </summary>
    public class FleetData : UserData
    {
        private List<FleetShip> _shipList = new List<FleetShip>();
        public string Type;


        public FleetData(bool shouldFileExist, Dictionary<ConfigData.ShipTypes, int> startingShips, int type) : base()
        {
            Type = ConfigData.FleetDataFilenames[type];
            defaultJsonData = MakeDefaultList(startingShips);

            dynamic json = SetupFile(shouldFileExist, ConfigData.FleetDataFilenames[type], (json) =>
            {
                ConfigData.IsFleetDataLoaded[type] = true;
                Debug.Log($"Loading ships for {Type}");
                LoadShipsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents())));
                Debug.Log($"Loaded {GetShips().Count} ships: {GetShips()[Utilities.RandomInt(GetShips().Count-1)].Name}");
            });

        }
        private string MakeDefaultList(Dictionary<ConfigData.ShipTypes, int> startingShips)
        {
            List<ConfigData.ShipTypes> shipTypes = startingShips.Keys.ToList();
            int id;
            shipTypes.ForEach((shipType) =>
            {
                
                int shipCount = startingShips.GetValueOrDefault(shipType);
                int side = ConfigData.Configuration.HumanSide;

                if (ConfigData.StartingSettings.BeeShipTypes.Contains(shipType))
                {
                    side = ConfigData.Configuration.BeeSide;
                }
                for (int i = 0; i < shipCount; i++)
                {
                    id = ConfigData.UserProgressData.GetNextFleetId();

                    AddShipToFleet(new FleetShip(id, shipType, false, false, 0, 0, 0, 0, 0, 0, 0));
                }
            });
            string json = ToJson();
            Debug.Log($"JSON for {Type} starting ships {GetShips().Count}, {GetShips().First().Name}");
            //Debug.Log(json);
            ClearFleet();
            return json;
        }
        private void LoadShipsFromJson(List<dynamic> jsonShips)
        {
            Debug.Log($"About to load {jsonShips.Count} ships for {Type}");
            jsonShips.ForEach((ship) =>
            {
                AddShipToFleet(new FleetShip((int) ship.i, (ConfigData.ShipTypes) ship.t, ((int)ship.s == 1 ? true : false), ((int)ship.d == 1 ? true : false), (int) ship.f, (int) ship.dd, (int) ship.r, (int) ship.k, (int) ship.b, (int) ship.w, (int)ship.m, (string)ship.n));
            });
        }
        public List<FleetShip> GetShips()
        {
            return _shipList;
        }
        public FleetShip GetFleetShip(long id)
        {
            return GetShips().Find((ship) => ship.Id == id);
        }
        public void AddShipToFleet(FleetShip ship)
        {
            if (!_shipList.Contains(ship)) {
                _shipList.Add(ship);
            }
            else
            {
                Debug.LogWarning($"Could not add fleetship to fleet because the Id already exists: {ship}");
            }
            
        }
        public void ClearFleet()
        {
            _shipList.Clear();
        }
        public override string ToJson()
        {
            string json = "[";
            GetShips().ForEach((s) => json += $"{s.ToJson()}, ");
            json = json.Remove(json.Length - 2);
            json += "]";
            return json;

        }
    }
}