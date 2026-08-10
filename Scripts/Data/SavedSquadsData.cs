using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Assets.Scripts.Scenes;

namespace Assets.Scripts.Data

{
    public class SavedSquadsData : UserData
    {
        // class that holds and manages storage for user fleet data
        private List<SavedSquad> _savedSquadsList = new List<SavedSquad>();
        public int Type;


        public SavedSquadsData(bool shouldFileExist, int type) : base()
        {
            Type = type;
            defaultJsonData = "[]";
            //Debug.Log($"defaultJSON: {defaultJsonData}");

            dynamic json = SetupFile(shouldFileExist, ConfigData.SavedSquadsDataFilenames[type], (json) =>
            {
                ConfigData.IsSavedSquadsDataLoaded[type] = true;
                _savedSquadsList.Clear();
                //Debug.Log($"Loaded saved squad data for {ConfigData.SavedSquadsDataFilenames[type]}");
                Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents()))).ForEach(s =>
                {
                    
                    AddSquad(s);
                });
                //Debug.Log($"Loaded ships {GetShips().Find((s => s.Id == Utilities.RandomInt(GetShips().Count - 1))).Name}");
            });

        }
        public override bool IsDataLoaded()
        {
            return base.IsDataLoaded() && ConfigData.IsFleetDataLoaded[Type];
        }
        public List<SavedSquad> GetSquads()
        {
            return _savedSquadsList;
        }
        public SavedSquad GetSquad(int id)
        {
            return _savedSquadsList.Find(s => s.Id == id);
        }
        public List<SquadShip> GetAllSquadShips()
        {
            List<SquadShip> ships = new List<SquadShip>();
            GetSquads().ForEach((squad) =>
            {
                squad.GetSquadShips().ForEach((squadShip) =>
                {
                    ships.Add(squadShip);
                });
            });
            return ships;
        }
        public List<FleetShip> GetAllFleetShips()
        {
            List<FleetShip> ships = new List<FleetShip>();
            GetSquads().ForEach((squad) =>
            {
                squad.GetSquadShips().ForEach((squadShip) =>
                {
                    ships.Add(squadShip.GetFleetShip());
                });
            });
            return ships;
        }
        public void AddSquad(SavedSquad squad)
        {
            //Debug.Log($"Loaded squad {squad.Name} at {squad.StartingPosition} at start of Add Squad call");

            if (!HasSquad(squad))
            {
                //Debug.Log($"Squad location before cloning: {squad.StartingPosition}");
                SavedSquad newSquad = (SavedSquad) squad.Clone();
                //Debug.Log($"Squad location after cloning: {newSquad.StartingPosition}");
                _savedSquadsList.Add(newSquad);
                //Debug.Log($"{squad} added to saved squads list");
            }
            else
            {
                //Debug.Log($"Squad exists: {squad.Id}, {squad.Name}");
            }
            
        }
        public void RemoveSquadFromList(SavedSquad squad)
        {
            if (squad == null)
            {
                return;
            }

            foreach (SquadShip squadShip in squad.GetSquadShips())
            {
                FleetShip fleetShip = squadShip.GetFleetShip();
                if (fleetShip != null)
                {
                    fleetShip.DoesBelongToSavedSquad = false;
                }
            }
            _savedSquadsList.Remove(squad);
        }
        public bool HasSquad(SavedSquad squad)
        {
            return _savedSquadsList.Find((s) => s.Id == squad.Id && s.Side == squad.Side) != null;
        }
        public void ClearSquads()
        {
            _savedSquadsList.Clear();
        }

        public override string ToJson()
        {
            string json = "[";
            GetSquads().ForEach((s) => json += $"{s.ToJson()}, ");
            if (GetSquads().Any())
            {
                json = json.Remove(json.Length - 2);
            }
            json += "]";
            return json;

        }
    }
}