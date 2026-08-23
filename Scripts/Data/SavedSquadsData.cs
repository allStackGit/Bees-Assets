using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Assets.Scripts.Data
{
    public class SavedSquadsData : UserData
    {
        private List<SavedSquad> _savedSquadsList = new List<SavedSquad>();
        public int Type;

        public SavedSquadsData(bool shouldFileExist, int type, bool forceCreateDefaults = false) : base()
        {
            Type = type;
            defaultJsonData = "[]";

            SetupFile(shouldFileExist, ConfigData.SavedSquadsDataFilenames[type], loadedData =>
            {
                _savedSquadsList.Clear();
                JArray json = AotJson.RequireArray(loadedData, ConfigData.SavedSquadsDataFilenames[type]);
                foreach (SavedSquad squad in AotJson.ParseSavedSquads(json))
                {
                    AddSquad(squad);
                }
                ConfigData.IsSavedSquadsDataLoaded[type] = true;
            }, forceCreateDefaults);
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
            GetSquads().ForEach(squad =>
            {
                squad.GetSquadShips().ForEach(squadShip =>
                {
                    ships.Add(squadShip);
                });
            });
            return ships;
        }

        public List<FleetShip> GetAllFleetShips()
        {
            List<FleetShip> ships = new List<FleetShip>();
            GetSquads().ForEach(squad =>
            {
                squad.GetSquadShips().ForEach(squadShip =>
                {
                    ships.Add(squadShip.GetFleetShip());
                });
            });
            return ships;
        }

        public void AddSquad(SavedSquad squad)
        {
            if (!HasSquad(squad))
            {
                SavedSquad newSquad = (SavedSquad)squad.Clone();
                _savedSquadsList.Add(newSquad);
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
            return _savedSquadsList.Find(s => s.Id == squad.Id && s.Side == squad.Side) != null;
        }

        public void ClearSquads()
        {
            _savedSquadsList.Clear();
        }

        public override string ToJson()
        {
            string json = "[";
            GetSquads().ForEach(s => json += $"{s.ToJson()}, ");
            if (GetSquads().Any())
            {
                json = json.Remove(json.Length - 2);
            }
            json += "]";
            return json;
        }
    }
}
