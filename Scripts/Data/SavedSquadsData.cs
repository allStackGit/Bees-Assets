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



        public SavedSquadsData(bool shouldFileExist, Scene scene) : base(scene)
        {
            // [alert] this should be equal to the JSON data for whatever starting squads there will be, currently two squads, one of three scouts and one
            // of three gunships

            defaultJsonData = "[]"; // [alert] need to change to actual defaults
            //Debugger.Log($"defaultJSON: {defaultJsonData}");

            dynamic json = SetupFile(shouldFileExist, ConfigData.SavedSquadsDataFilename, (json) =>
            {
                ConfigData.IsSavedSquadsDataLoaded = true;

                LoadSquadsFromJson(Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(file.GetContents())));
                //Debugger.Log($"Loaded ships {GetShips().Find((s => s.Id == Utilities.RandomInt(GetShips().Count - 1))).Name}");
            });

        }
        private void LoadSquadsFromJson(List<dynamic> jsonSquads)
        {
            jsonSquads.ForEach((squad) =>
            {
                Color color = new Color((float) squad.Color.r, (float) squad.Color.g, (float) squad.Color.b, (float) squad.Color.a);
                SquadStatBlock Stats = new SquadStatBlock((string) squad.Stats.Commander, (int) squad.Stats.BattlesFought, (int) squad.Stats.BattlesWon,
                    (int) squad.Stats.ShipsLost, (int) squad.Stats.DamageDone, (int) squad.Stats.DamageReceived, (int) squad.Stats.Kills);
                SavedSquad savedSquad = new SavedSquad((int)squad.Id, (int) squad.Side, (string)squad.Name, new Vector2((float) squad.StartingPosition.x, (float) squad.StartingPosition.y),
                    (bool) squad.CeaseFire, (bool) squad.IsMatchingSpeed, (string) squad.ChosenShootingStrategy, color, Stats);
                //Debugger.Log($"Squad ships, {savedSquad.Name}, {squad.Ships}");
                //Vector2 startingPosition = new Vector2(savedSquad.StartingPosition.x, savedSquad.StartingPosition.y);
                List<dynamic> ships = squad.Ships.ToObject<List<dynamic>>();

                ships.ForEach((ship) =>
                {
                   
                    savedSquad.AddShipToSquad(new SquadShip((int) ship.FleetId, (string) ship.ShipType, new Vector2((float) ship.Offset.x, (float) ship.Offset.y), 
                     savedSquad));

                });
                //Debugger.Log($"Loaded squad {squad.Name} at {squad.StartingPosition} at before Add Squad call");
                //savedSquad.StartingPosition = startingPosition;
                AddSquad(savedSquad);

            });
            //Debugger.Log("Finished loading the squads from list");
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
                squad.GetShips().ForEach((squadShip) =>
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
                squad.GetShips().ForEach((squadShip) =>
                {
                    ships.Add(squadShip.GetFleetShip());
                });
            });
            return ships;
        }
        public void AddSquad(SavedSquad squad)
        {
            //Debugger.Log($"Loaded squad {squad.Name} at {squad.StartingPosition} at start of Add Squad call");

            if (!HasSquad(squad))
            {
                //Debugger.Log($"Squad location before cloning: {squad.StartingPosition}");
                SavedSquad newSquad = (SavedSquad) squad.Clone();
                //Debugger.Log($"Squad location after cloning: {newSquad.StartingPosition}");
                _savedSquadsList.Add(newSquad);
            }
            else
            {
                Debugger.Log($"Squad exists: {squad.Id}, {squad.Name}");
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