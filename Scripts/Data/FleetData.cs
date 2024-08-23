using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Scenes;

namespace Assets.Scripts.Data
{
    /// <summary>
    /// class that holds and manages storage for user fleet data
    /// </summary>
    public class FleetData : UserData
    {
        private List<FleetShip> _shipList = new List<FleetShip>();


        public FleetData(bool shouldFileExist, Dictionary<string, int> startingShips) : base()
        {
            defaultJsonData = MakeDefaultList(startingShips);

            dynamic json = SetupFile(shouldFileExist, ConfigData.FleetDataFilename, (json) =>
            {
                ConfigData.IsFleetDataLoaded = true;
                //Debug.Log("Updated config file");
                LoadShipsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents())));
                //Debug.Log($"Loaded ships {GetShips().Find((s => s.Id == Utilities.RandomInt(GetShips().Count-1))).Name}");
            });

        }
        private string MakeDefaultList(Dictionary<string, int> startingShips)
        {
            List<string> shipTypes = startingShips.Keys.ToList();
            int id = 0;
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
                    
                    bool isVisible = false;
                    if (ConfigData.InitialVisibleShips.Contains(id))
                    {
                        isVisible = true;
                    }
                    AddShipToFleet(new FleetShip(id, side, $"{shipType} #{id}", shipType, false, isVisible, false, 0, 0, 0, 0, 0, 0, 0));
                    id++;
                }
            });
            string json = ToJson();
            //Debug.Log($"JSON for starting ships {GetShips().Count}, {GetShips().First().Name}");
            //Debug.Log(json);
            ClearFleet();
            return json;
        }
        private void LoadShipsFromJson(List<dynamic> jsonShips)
        {
            jsonShips.ForEach((ship) =>
            {
                if (ship.HasCachedSprite == null) // [alert] this is just here to account for old data that doesn't have this filled in yet
                {
                    ship.HasCachedSprite = "false";
                }
                AddShipToFleet(new FleetShip((int) ship.Id, (int) ship.Side, (string) ship.Name, (string) ship.Type, (bool) ship.HasCachedSprite, (bool) ship.IsVisibleToUser, (bool) ship.IsDead, (int) ship.ShotsFired,
                    (int) ship.DamageDone, (int) ship.DamageReceived, (int) ship.Kills, (int) ship.BattlesFought, (int) ship.BattlesWon, (int)ship.MineralsMined));
            });
        }
        public List<FleetShip> GetShips()
        {
            return _shipList;
        }
        public List<FleetShip> GetShipsByType(string type)
        {
            return GetShips().Where((ship) => ship.Type== type).ToList();
        }
        public FleetShip GetFleetShip(int id)
        {
            return GetShips().Find((ship) => ship.Id == id);
        }
        public void AddShipToFleet(FleetShip ship)
        {
            FleetShip exists = _shipList.Find((s) => s.Id == ship.Id);
            if (exists == null) {
                _shipList.Add(ship);
            }
            
        }
        public void RemoveShipFromFleet(FleetShip ship)
        {
            _shipList.Remove(ship);
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