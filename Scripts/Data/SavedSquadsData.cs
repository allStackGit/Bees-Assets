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
        // class that holds and manages storage for user fleet ships and saved squads
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

            SavedSquad storedSquad = _savedSquadsList.FirstOrDefault(candidate =>
                candidate.Id == squad.Id && candidate.Side == squad.Side);
            if (storedSquad == null)
            {
                return;
            }

            // A deleted player-created squad is a source of replacement ships before it is a
            // source of free-fleet ships. Existing squads retain dead SquadShip entries as casualty
            // slots, so fill matching slots first while preserving their authored formation offsets.
            // Negative-ID setup/encounter squads are deliberately excluded from this behavior.
            bool refillCasualtySlots = storedSquad.Id >= 0 && storedSquad.HasBeenSavedToStorage;
            List<FleetShip> releasedShips = storedSquad.GetSquadShips()
                .Select(squadShip => squadShip.GetFleetShip())
                .Where(fleetShip => fleetShip != null)
                .ToList();

            _savedSquadsList.Remove(storedSquad);

            foreach (FleetShip releasedShip in releasedShips)
            {
                if (refillCasualtySlots && releasedShip.IsShipAlive() &&
                    TryFillCasualtySlot(releasedShip, storedSquad.Side))
                {
                    continue;
                }

                // No surviving squad needs this ship, so it is now genuinely available to the fleet.
                releasedShip.DoesBelongToSavedSquad = false;
            }
        }

        private bool TryFillCasualtySlot(FleetShip releasedShip, int side)
        {
            foreach (SavedSquad destinationSquad in _savedSquadsList.Where(candidate => candidate.Side == side))
            {
                SquadShip casualtySlot = destinationSquad.GetDeadShips()
                    .FirstOrDefault(deadShip => deadShip.ShipType == releasedShip.Type);
                if (casualtySlot == null)
                {
                    continue;
                }

                Vector2 preservedOffset = casualtySlot.Offset;
                destinationSquad.RemoveShipFromSquad(casualtySlot, false);
                destinationSquad.AddShipToSquad(new SquadShip(releasedShip, preservedOffset));
                return true;
            }

            return false;
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