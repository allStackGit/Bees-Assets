

using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using Assets.Scripts.Settings;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts
{
    /// <summary>
    /// Handles all the combined ship data for both fleet ships and saved squads
    /// </summary>
    public class Ships
    {
        private FleetData _fleetData;
        private SavedSquadsData _savedSquadsData;

        public Ships(FleetData fleetData, SavedSquadsData savedSquadsData) { 
            _fleetData = fleetData;
            _savedSquadsData = savedSquadsData;
        }

        // get fleet methods
        public void AddShipsToFleet(ConfigData.ShipTypes shipType, int shipCount)
        {
            int id = ConfigData.UserProgressData.GetNextFleetId();
            for (int i = 0; i < shipCount; i++)
            {
                _fleetData.AddShipToFleet(new FleetShip(id, shipType, false, true, 0, 0, 0, 0, 0, 0, 0));
                id++;
            }
        }
        public List<FleetShip> GetFleetShips()
        {
            return _fleetData.GetShips();
        }
        public List<FleetShip> GetAliveFleetShipsBySide(int side)
        {
            return GetAliveShips().Where((s) => s.Side == side).ToList();
        }
        public FleetShip GetFleetShip(long id)
        {
            return GetFleetShips().Find((ship) => ship.Id == id);
        }
        public List<FleetShip> GetAvailableShips()
        {
            return GetFleetShips().Where((ship) => ship.IsShipAlive() && !ship.DoesBelongToSavedSquad && !IsShipInSquad(ship)).ToList();
        }
        public List<FleetShip> GetAliveShips()
        {
            return GetFleetShips().Where((ship) => ship.IsShipAlive()).ToList();
        }
        public List<FleetShip> GetShipsOfType(ConfigData.ShipTypes type)
        {
            return GetFleetShips().Where((ship) => ship.Type == type).ToList();
        }
        public List<FleetShip> GetAvailableShipsOfType(ConfigData.ShipTypes type)
        {
            return GetAvailableShips().Where((ship) => ship.Type == type).ToList();
        }
        public FleetShip GetFirstAvailableShipOfType(ConfigData.ShipTypes type)
        {
            return GetAvailableShips().Where((ship) => ship.Type == type).FirstOrDefault();
        }
        public List<FleetShip> GetAliveShipsOfType(ConfigData.ShipTypes type)
        {
            return GetAliveShips().Where((ship) => ship.Type == type).ToList();
        }
        public int GetShipRanking(FleetShip ship, string statType) {
            List<FleetShip> rankings = new List<FleetShip>();
            int ranking = 0;
            int side = ship.Side;
            switch (statType)
            {
                case "ShotsFired":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.ShotsFired).ToList();
                    ranking = rankings.IndexOf(ship)+1;
                    while (ranking > 1 && ship.ShotsFired == rankings.ElementAt(ranking - 2).ShotsFired)
                    {
                        ranking--;
                    }
                    break;
                case "DamageDone":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.DamageDone).ToList();
                    ranking = rankings.IndexOf(ship) + 1;
                    while (ranking > 1 && ship.DamageDone == rankings.ElementAt(ranking - 2).DamageDone)
                    {
                        ranking--;
                    }
                    break;
                case "DamageReceived":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.DamageReceived).ToList();
                    ranking = rankings.IndexOf(ship) + 1;
                    while (ranking > 1 && ship.DamageReceived == rankings.ElementAt(ranking - 2).DamageReceived)
                    {
                        ranking--;
                    }
                    break;
                case "Kills":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.Kills).ToList();
                    ranking = rankings.IndexOf(ship) + 1;
                    while (ranking > 1 && ship.Kills == rankings.ElementAt(ranking - 2).Kills)
                    {
                        ranking--;
                    }
                    break;
                case "Record":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.BattlesFought > 0 ? ((double)s.BattlesWon/(double)s.BattlesFought) : 0).ToList();
                    ranking = rankings.IndexOf(ship) + 1;
                    
                    if (ranking > 1)
                    {
                        FleetShip shipAbove = rankings.ElementAt(ranking - 2);
                        double shipAboveValue = shipAbove.BattlesFought > 0 ? (double) shipAbove.BattlesWon / (double) shipAbove.BattlesFought : 0;
                        double shipValue = ship.BattlesFought > 0 ? (double) ship.BattlesWon / (double) ship.BattlesFought : 0;
                        bool moved = false;

                        while (ranking > 1 && shipValue == shipAboveValue)
                        {
                            shipAbove = rankings.ElementAt(ranking - 2);
                            shipAboveValue = shipAbove.BattlesFought > 0 ? (double)shipAbove.BattlesWon / (double) shipAbove.BattlesFought : 0;
                            shipValue = shipValue = ship.BattlesFought > 0 ? (double)ship.BattlesWon / (double) ship.BattlesFought : 0;
                            //Debug.Log($"Ranking for {ship.Name}: {ranking}, {shipValue}, {shipAbove.Name}: {shipAboveValue}");
                            moved = true;
                            ranking--;
                        }
                        if (shipValue < shipAboveValue && moved)
                        {
                            ranking++;
                        }
                    }
                    
                    break;
                case "Minerals Mined":
                    rankings = GetAliveFleetShipsBySide(side).OrderByDescending((s) => s.MineralsMined).ToList();
                    ranking = rankings.IndexOf(ship) + 1;
                    while (ranking > 1 && ship.MineralsMined == rankings.ElementAt(ranking - 2).MineralsMined)
                    {
                        ranking--;
                    }
                    break;

            }
            return ranking;
        }


        // Get squad methods
        public List<SquadShip> GetSquadShips()
        {
            return _savedSquadsData.GetAllSquadShips();
        }
        public List<SavedSquad> GetSavedSquads()
        {
            return _savedSquadsData.GetSquads();
        }
        public List<SavedSquad> GetSavedSquadsBySide(int side)
        {
            return GetSavedSquads().Where((s) => s.Side == side).ToList();
        }
        public SavedSquad GetSavedSquad(long id)
        {
            return GetSavedSquads().Find((squad) => squad.Id == id);
        }
        public SavedSquad GetSavedSquadFromFleetShip(FleetShip ship)
        {
            foreach (SavedSquad squad in GetSavedSquads())
            {
                if (squad.HasShip(ship))
                {
                    return squad;
                }
            }
            return null;
        }
        public int GetSquadRanking(SavedSquad squad, string statType)
        {
            List<SavedSquad> rankings = new List<SavedSquad>();
            int ranking = 0;
            int side = squad.Side;
            switch (statType)
            {
                case "DamageDone":
                    rankings = GetSavedSquadsBySide(side).OrderByDescending((s) => s.Stats.DamageDone).ToList();
                    ranking = rankings.IndexOf(squad) + 1;
                    while (ranking > 1 && squad.Stats.DamageDone == rankings.ElementAt(ranking - 2).Stats.DamageDone)
                    {
                        ranking--;
                    }
                    break;
                case "DamageReceived":
                    rankings = GetSavedSquadsBySide(side).OrderByDescending((s) => s.Stats.DamageReceived).ToList();
                    ranking = rankings.IndexOf(squad) + 1;
                    while (ranking > 1 && squad.Stats.DamageReceived == rankings.ElementAt(ranking - 2).Stats.DamageReceived)
                    {
                        ranking--;
                    }
                    break;
                case "Kills":
                    rankings = GetSavedSquadsBySide(side).OrderByDescending((s) => s.Stats.Kills).ToList();
                    ranking = rankings.IndexOf(squad) + 1;
                    while (ranking > 1 && squad.Stats.Kills == rankings.ElementAt(ranking - 2).Stats.Kills)
                    {
                        ranking--;
                    }
                    break;
                case "ShipsLost":
                    rankings = GetSavedSquadsBySide(side).OrderByDescending((s) => s.Stats.ShipsLost).ToList();
                    ranking = rankings.IndexOf(squad) + 1;
                    while (ranking > 1 && squad.Stats.ShipsLost == rankings.ElementAt(ranking - 2).Stats.ShipsLost)
                    {
                        ranking--;
                    }
                    break;
                case "Record":
                    rankings = GetSavedSquadsBySide(side).OrderByDescending((s) => s.Stats.BattlesFought > 0 ? ((double) s.Stats.BattlesWon / (double) s.Stats.BattlesFought) : 0).ToList();
                    ranking = rankings.IndexOf(squad) + 1;
                    if (ranking > 1)
                    {
                        SavedSquad squadAbove = rankings.ElementAt(ranking - 2);
                        double squadAboveValue = squadAbove.Stats.BattlesFought > 0 ? (double) squadAbove.Stats.BattlesWon / squadAbove.Stats.BattlesFought : 0;
                        double squadValue = squad.Stats.BattlesFought > 0 ? (double) squad.Stats.BattlesWon / squad.Stats.BattlesFought : 0;
                        bool moved = false;
                        //Debug.Log($"Ranking for {squad.Name}: {ranking}, {squadValue}, {squadAbove.Name}: {squadAboveValue}");
                        //Debug.Log("____________");
                        while (ranking > 1 && squadValue == squadAboveValue)
                        {
                            squadAbove = rankings.ElementAt(ranking - 2);
                            squadAboveValue = squadAbove.Stats.BattlesFought > 0 ? (double) squadAbove.Stats.BattlesWon / squadAbove.Stats.BattlesFought : 0;
                            squadValue = squad.Stats.BattlesFought > 0 ? (double) squad.Stats.BattlesWon / squad.Stats.BattlesFought : 0;
                            //Debug.Log($"Ranking for {squad.Name}: {ranking}, {squadValue}, {squadAbove.Name}: {squadAboveValue}");
                            moved = true;
                            ranking--;

                        }
                        if (squadValue < squadAboveValue && moved)
                        {
                            ranking++;
                        }
                    }
                    
                    break;

            }

            return ranking;
        }

        // check squad methods
        public bool IsShipInSquad(FleetShip ship)
        {
            foreach (SavedSquad squad in GetSavedSquads())
            {
                if (squad.HasShip(ship))
                {
                    ship.DoesBelongToSavedSquad = true;
                    return true;
                }
            }
            return false;
        }
        public bool DoesSquadExist(long id)
        {
            return GetSavedSquad(id) != null;
        }


        // add squad methods
        public void AddSquad(SavedSquad squad)
        {
            _savedSquadsData.AddSquad(squad);
        }

        // squad utility methods
        /// <summary>
        /// Replaces the dead ships is a SavedSquad with new ships from the fleet if available. If Level.ReplaceDeadShips isn't true then there aren't any dead ships to replace
        /// </summary>
        public void ReplaceDeadSquadShips(bool replaceAISide)
        {
            //Debug.Log($"Replacing dead squad ships");
            List<SavedSquad> squads = replaceAISide ? GetSavedSquads() : GetSavedSquadsBySide(ConfigData.Configuration.UserSide);
            bool replaced = false;
            squads.ForEach((squad) =>
            {
                if (ReplaceDeadShipsInSquad(squad))
                {
                    replaced = true;
                }
            });

            if (replaced)
            {
                Debug.Log("Replaced dead ships");
                SaveSquadData();
            }

        }
        public bool ReplaceDeadShipsInSquad(SavedSquad squad)
        {
            bool replaced = false;
            //if (squad.GetDeadShips().Count == 0)
            //{
            //    Debug.Log($"There are no dead ships for {squad}");
            //}
            squad.GetDeadShips().ForEach((squadShip) =>
            {
                FleetShip replacement = GetFirstAvailableShipOfType(squadShip.GetFleetShip().Type);
                if (replacement != null)
                {
                    if (squadShip.GetFleetShip().HasCachedSprite)
                    {
                        replacement.HasCachedSprite = true;
                    }
                    Debug.Log($"Replaced dead {squadShip.GetFleetShip()} with {replacement}");
                    squadShip.FleetId = replacement.Id;
                    replaced = true;
                }
                //else
                //{
                //    Debug.Log($"Could not replace {squadShip} because there were no available replacements");
                //}
            });
            return replaced;
        }
        public void RemoveSquad(SavedSquad squad)
        {
            if (DoesSquadExist(squad.Id))
            {
                _savedSquadsData.RemoveSquadFromList(squad);
            }
        }

        public SavedSquad BuildNewSquad(string name, int side, ConfigData.ShipTypes shipType, int shipCount)
        {

            Vector2[] offsets = ConfigData.GeneratedSquadFormationOffsets4x4;
            if (shipCount == 1)
            {
                offsets[0] = Vector2.zero;
            }
            else
            {
                if (shipCount <= 4)
                {
                    if (ConfigData.LargeShips.Contains(shipType))
                    {
                        offsets = ConfigData.GeneratedSquadFormationOffsets2x2Large;
                    }
                    else
                    {
                        offsets = ConfigData.GeneratedSquadFormationOffsets2x2;
                    }
                }
                if (ConfigData.MediumShips.Contains(shipType))
                {
                    offsets = ConfigData.GeneratedSquadFormationOffsets4x4Medium;
                }
            }



            int squadId = ConfigData.UserProgressData.GetNextSavedSquadId();
            SavedSquad savedSquad = new SavedSquad(squadId, side, name, Vector2.zero, false, false, DefaultShootingStrategy, UnsetColor, null);
            //List<FleetShip> fleetShips = CurrentShips.GetAvailableShipsOfType(shipType);
            for (int i = 0; i < shipCount; i++) 
            {
                FleetShip ship = CurrentShips.GetFirstAvailableShipOfType(shipType);
                if (ship != null)
                {
                    savedSquad.AddShipToSquad(new SquadShip(ship.Id, ship.Type, offsets[i]));
                }
                else
                {
                    Debug.LogError($"Could not fully create {name} because there were only {i}/{shipCount} ships of {shipType}");
                    Debug.Log(Utilities.ListToString(CurrentShips.GetFleetShips().Where((fs) => fs.Type == shipType).ToList()));
                }
            }
            //Debug.Log($"Built {savedSquad}");
            CurrentShips.AddSquad(savedSquad);

            return savedSquad;
        }

        public SavedSquad GetSquadByComposition(Level level, ConfigData.ShipTypes shipType, int shipCount, bool canHaveFewerShips = false, bool canHaveMoreShips = false)
        {
            SavedSquad savedSquad = GetSavedSquads().Find((squad) => !squad.IsLoadedIntoLevel && squad.GetSquadShips().Count == shipCount && squad.GetAliveSquadShips().All((s) => s.ShipType == shipType) && squad.GetAliveSquadShips().Count > 0); ;
            if (savedSquad == null)
            {
                if (!canHaveFewerShips && !canHaveMoreShips)
                {
                    Debug.LogError($"No squad found with {shipCount} ships of type {shipType}");
                }
                else if (canHaveFewerShips)
                {
                    savedSquad = GetSavedSquads().Find((squad) => !squad.IsLoadedIntoLevel && squad.GetSquadShips().Count <= shipCount && squad.GetAliveSquadShips().All((s) => s.ShipType == shipType) && squad.GetAliveSquadShips().Count > 0);
                    if (savedSquad == null && canHaveMoreShips)
                    {
                        savedSquad = GetSavedSquads().Find((squad) => !squad.IsLoadedIntoLevel && squad.GetSquadShips().Count >= shipCount && squad.GetAliveSquadShips().All((s) => s.ShipType == shipType) && squad.GetAliveSquadShips().Count > 0);
                    }
                }
                else if (canHaveMoreShips)
                {
                    savedSquad = GetSavedSquads().Find((squad) => !squad.IsLoadedIntoLevel && squad.GetSquadShips().Count >= shipCount && squad.GetAliveSquadShips().All((s) => s.ShipType == shipType) && squad.GetAliveSquadShips().Count > 0);

                }                
            }
            return savedSquad;

        }
        public SavedSquad GetSquadByComposition(ConfigData.ShipTypes shipType)
        {
            return GetSavedSquads().FirstOrDefault((squad) => squad.GetSquadShips().All((s) => s.ShipType == shipType) && squad.GetSquadShips().Count <= 4);
        }


        // alias underlying data methods
        public void SaveSquadData()
        {
            _savedSquadsData.Save();
        }
        public void SaveFleetData()
        {
            //Debug.Log($"Saving the fleet data");
            _fleetData.Save();
        }


    }
}