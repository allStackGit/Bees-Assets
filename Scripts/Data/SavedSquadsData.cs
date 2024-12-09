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



        public SavedSquadsData(bool shouldFileExist) : base()
        {
            // [alert] this should be equal to the JSON data for whatever starting squads there will be, currently two squads, one of three scouts and one
            // of three gunships

            defaultJsonData = "[]"; // [alert] need to change to actual defaults
            //Debug.Log($"defaultJSON: {defaultJsonData}");

            dynamic json = SetupFile(shouldFileExist, ConfigData.SavedSquadsDataFilename, (json) =>
            {
                ConfigData.IsSavedSquadsDataLoaded = true;

                Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents()))).ForEach(s =>
                {
                    AddSquad(s);
                });
                //Debug.Log($"Loaded ships {GetShips().Find((s => s.Id == Utilities.RandomInt(GetShips().Count - 1))).Name}");
            });

        }
        public List<SavedSquad> GetSquads()
        {
            return _savedSquadsList;
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
            }
            else
            {
                Debug.Log($"Squad exists: {squad.Id}, {squad.Name}");
            }
            
        }
        public void RemoveSquadFromList(SavedSquad squad)
        {
            foreach (SavedSquad savedSquad in _savedSquadsList)
            {
                if (savedSquad.Equals(squad))
                {
                    _savedSquadsList.Remove(savedSquad);
                    return;
                }
            }
        }
        public bool HasSquad(SavedSquad squad)
        {
            return _savedSquadsList.Find((s) => s.Id == squad.Id) != null;
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