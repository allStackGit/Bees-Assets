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
        private readonly Dictionary<ConfigData.ShipTypes, int> _startingShips;
        public string Type;


        public FleetData(bool shouldFileExist, Dictionary<ConfigData.ShipTypes, int> startingShips, int type, bool forceCreateDefaults = false) : base()
        {
            Type = ConfigData.FleetDataFilenames[type];
            _startingShips = startingShips != null
                ? new Dictionary<ConfigData.ShipTypes, int>(startingShips)
                : new Dictionary<ConfigData.ShipTypes, int>();

            // Do not allocate persistent FleetIds merely to prepare fallback JSON. User progress
            // is loaded asynchronously and may not have applied its persisted FleetId yet. Generate
            // the fallback only if it is actually needed and the allocator has been synchronized.
            defaultJsonData = "";

            dynamic json = SetupFile(shouldFileExist, ConfigData.FleetDataFilenames[type], (json) =>
            {
                //Debug.Log($"Loading ships for {Type}");
                _shipList.Clear();
                LoadShipsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents())));
                ConfigData.IsFleetDataLoaded[type] = true;
                //Debug.Log($"Loaded {GetShips().Count} ships: {GetShips()[Utilities.RandomInt(GetShips().Count-1)]}");
            }, forceCreateDefaults);

        }

        public override string GetDefaultJson()
        {
            if (!string.IsNullOrWhiteSpace(defaultJsonData))
            {
                return defaultJsonData;
            }

            if (!TrySynchronizeFleetIdAllocator())
            {
                // A missing fleet response can beat the user-progress response because server
                // reads are asynchronous. Returning the waiting sentinel makes DataFile defer the
                // write; the existing request retry path will ask again after progress is ready.
                return ConfigData.WaitingMessage;
            }

            defaultJsonData = MakeDefaultList(_startingShips);
            return defaultJsonData;
        }

        private bool TrySynchronizeFleetIdAllocator()
        {
            if (ConfigData.UserProgressData == null)
            {
                return false;
            }

            // The DataFile can have received progress JSON before UserProgressData.WaitForData()
            // runs its loader callback. Reconcile directly from that raw payload so local first-run
            // creation and response reordering both use the persisted/default FleetId safely.
            object progressObject = ConfigData.UserProgressData.GetDataFile()?.GetJsonObject();
            if (progressObject is JObject progressJson && progressJson["FleetId"] != null)
            {
                int persistedFleetId = progressJson["FleetId"].Value<int>();
                if (ConfigData.UserProgressData.FleetId < persistedFleetId)
                {
                    ConfigData.UserProgressData.FleetId = persistedFleetId;
                }
                return true;
            }

            // If the callback already ran, the strongly typed field is authoritative even when
            // tooling supplied the data without retaining a JObject.
            return ConfigData.IsUserProgressDataLoaded;
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
            //Debug.Log($"JSON for {Type} starting ships {GetShips().Count}, {GetShips().First().Name}");
            //Debug.Log(json);
            ClearFleet();
            return json;
        }
        private void LoadShipsFromJson(List<dynamic> jsonShips)
        {
            //Debug.Log($"About to load {jsonShips.Count} ships for {Type}");
            jsonShips.ForEach((ship) =>
            {
                int mineralsMined = ship.m != null ? (int)ship.m : 0;
                string name = ship.n != null ? (string)ship.n : "";
                AddShipToFleet(new FleetShip((int) ship.i, (ConfigData.ShipTypes) ship.t, ((int)ship.s == 1 ? true : false), ((int)ship.d == 1 ? true : false), (int) ship.f, (int) ship.dd, (int) ship.r, (int) ship.k, (int) ship.b, (int) ship.w, mineralsMined, name));
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
            return new JArray(GetShips().Select(ship => JToken.Parse(ship.ToJson())))
                .ToString(Formatting.None);

        }
    }
}