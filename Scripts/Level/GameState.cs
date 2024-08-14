using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
//using System.Security.Policy;
using System.Text.RegularExpressions;
using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class GameState : MonoBehaviour
    {
        private List<Ship> _ships = new List<Ship>();
        private List<Squad> _squads = new List<Squad>();
        private Queue<Squad> _squadsAwaitingCommands = new Queue<Squad>();
        private List<StoredCommand> _pastCommands = new List<StoredCommand>();
        private List<Squad> _selectedSquads = new List<Squad>();
        private List<Obstacle> _obstacles = new List<Obstacle>();

        public int EntityCount, IdCount, UserCommands, AICommands;
        public bool IsPaused;
        public bool GameOver = false;
        public bool LevelEnded = false;
        public int[] InitialTsv = new int[] { 0, 0 };
        public List<SpottedShip>[] SpottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };
        public int[] OriginalSquadCounts = new int[] { 0, 0 };
        public LevelStage Level;
        public HashSet<Ship>[] VisionCache = new HashSet<Ship>[] { new HashSet<Ship>(), new HashSet<Ship>() };
        public Dictionary<long, HashSet<Ship>>[] HivemindShips = new Dictionary<long, HashSet<Ship>>[] { new Dictionary<long, HashSet<Ship>>(), new Dictionary<long, HashSet<Ship>>() };
        public bool HasWarpGates;
        //public bool[] HasMiningShips = new bool[2];




        public void Setup(LevelStage level)
        {
            Level = level;
            // Debug.Log("Game State has been setup");
        }
        public void ResetState()
        {
            _ships.Clear();
            _squads.Clear();
            _squadsAwaitingCommands.Clear();
            _pastCommands.Clear();
            _selectedSquads.Clear();
            _obstacles.Clear();
            SpottedShips[0].Clear();
            SpottedShips[1].Clear();
            InitialTsv = new int[] { 0, 0 };
            OriginalSquadCounts = new int[] { 0, 0 };
            HivemindShips = new Dictionary<long, HashSet<Ship>>[] { new Dictionary<long, HashSet<Ship>>(), new Dictionary<long, HashSet<Ship>>() };
            VisionCache = new HashSet<Ship>[] { new HashSet<Ship>(), new HashSet<Ship>() };
            IdCount = 0;
        }

        public void AddSpottedShip(Ship ship, Ship spotter)
        {
            SpottedShip spottedShip = new SpottedShip(ship, spotter.Id);
            if (!SpottedShips[spotter.Side - 1].Any((s) => s.Ship.Equals(ship)))
            {
                SpottedShips[spotter.Side - 1].Add(spottedShip);
            }
        }
        public void AddSpottedShips(List<Ship> spottedShips, Ship spotter)
        {
            List<SpottedShip> ships = SpottedShips[spotter.Side - 1];
            int maxExistingShips = spottedShips.Count;
            int maxNewShips = ships.Count;
            for (int i = 0; i < maxExistingShips; i++)
            {

                bool duplicate = false;
                Ship spottedShip = spottedShips[i];
                for (int ship = 0; ship < maxNewShips && !duplicate; ship++)
                {
                    duplicate = ships[ship].Ship.Id == spottedShip.Id;
                }
                ships.Add(new SpottedShip(spottedShip, spotter.Id));
            }
        }
        public int AddEntity()
        {
            return EntityCount++;
        }
        public int AddUserCommand()
        {
            return UserCommands++;
        }
        public int GetId()
        {
            return IdCount++;
        }
        public void AddShip(Ship ship)
        {
            // Debug.Log($"{ship.name} has been added to the state");
            _ships.Add(ship);
        }
        public void AddSquad(Squad squad)
        {
            _squads.Add(squad);
            OriginalSquadCounts[squad.Side - 1]++;
        }
        public void AddObstacle(Obstacle obstacle)
        {
            _obstacles.Add(obstacle);
        }
        public void RemoveShip(Ship ship)
        {
            _ships.Remove(ship);
            if (IsShipExtinct(ship.ShipType))
            {
                if (Level.Audio != null)
                {
                    Level.Audio.MuteSource(Level.Audio.BeesLoops.GetValueOrDefault(ship.ShipType));
                    Level.Audio.MuteSource(Level.Audio.BeesIntros.GetValueOrDefault(ship.ShipType));
                }

            }
        }
        public void RemoveObstacle(Obstacle obstacle)
        {
            _obstacles.Remove(obstacle);
        }
        public void AddCommand(Command command)
        {
            //_commands.Add(command);
            _pastCommands.Add(new StoredCommand(command));
            AICommands++;
            ConfigData.__HivemindCommands++;
        }

        public List<Obstacle> GetObstacles()
        {
            return _obstacles;
        }
        /// <summary>
        /// Gets all the ships on the level or for a certain side if specified
        /// </summary>
        /// <param name="side"></param>
        /// <returns></returns>
        public List<Ship> GetShips(int side = 0)
        {
            return side switch
            {
                1 => _ships.Where(ship => ship.Side == 1).ToList(),
                2 => _ships.Where(ship => ship.Side == 2).ToList(),
                _ => _ships
            };
        }
        public HashSet<Ship> GetShipsVisibleToHiveMind(int side)
        {

            VisionCache[side - 1] = HivemindShips[side - 1].Aggregate(new HashSet<Ship>(), (sum, dictionary) => {
                sum.UnionWith(dictionary.Value.Where((ship) => ship != null && !ship.IsDead));
                return sum;
            });
            
            return VisionCache[side - 1];
        }

        public HashSet<string> GetHumanShipTypes()
        {
            return GetHumanShips().Select((s) => s.ShipType).ToHashSet();
        }
        public List<Ship> GetBeeShips()
        {
            return GetShips(ConfigData.Configuration.BeeSide);
        }
        public HashSet<string> GetBeeShipTypes()
        {
            return GetBeeShips().Select((s) => s.ShipType).ToHashSet();
        }
        public int GetTsvBySide(int side)
        {
            return GetSquadsBySide(side).Sum((s) => s.Tsv);
        }
        public List<Ship> GetHumanShips()
        {
            return GetShips(ConfigData.Configuration.HumanSide);
        }
        /// <summary>
        /// Gets all ships that do not match the side given
        /// </summary>
        /// <param name="side"></param>
        /// <returns></returns>
        public List<Ship> GetAllEnemyShips(int side)
        {
            return _ships.Where(ship => ship.Side != side).ToList();
        }
        public Ship GetShipById(long id)
        {
            return _ships.FirstOrDefault(ship => ship.Id == id);
        }
        public bool IsShipExtinct(string shipType)
        {
            return !GetShips().Any((s) => s.ShipType == shipType);
        }

        public List<Squad> GetSquadsVisibleToHiveMind(int side = 0)
        {
            return GetShipsVisibleToHiveMind(side).Select((ship) => ship.Squad).ToList();
        }
        public List<Squad> GetSelectedSquads()
        {
            return _selectedSquads.Where((squad) => squad != null).ToList();
        }
        public void AddSelectedSquad(Squad squad)
        {
            if (squad != null && squad.IsUserControlled)
            {
                _selectedSquads.Add(squad);
                squad.MoveSquadBox();
                if (Level.DoesUserHaveController)
                {
                    Level.Menus.ActionBox.SetupForSquad();
                }
            }

        }
        public void SelectSquads(List<Squad> squads)
        {
            ClearSelectedSquads();
            squads.ForEach((squad) =>
            {
                AddSelectedSquad(squad);
            });
        }
        public void ClearSelectedSquads()
        {
            _selectedSquads.ForEach((squad) =>
            {
                squad.DeactivateSquadBox();
            });
            _selectedSquads.Clear();
        }
        public void SelectSquad(Squad squad)
        {
            //Debug.Log($"Selecting squad {squad.Name}");
            if (squad != null)
            {
                ClearSelectedSquads();
                AddSelectedSquad(squad);
            }

        }
        public Squad GetSquadByNumber(int side, int squadNumber)
        {
            return GetSquadsBySide(side).FirstOrDefault(squad => squad.SquadNumber == squadNumber);
        }
        public Squad GetSquadById(long Id)
        {
            return _squads.FirstOrDefault(squad => squad.Id == Id);
        }
        public List<Squad> GetAllSquads()
        {
            return _squads.Where(squad => squad != null && !squad.IsDead).ToList();
        }
        public List<Squad> GetSquadsBySide(int side)
        {
            return GetAllSquads().Where(squad => squad.Side == side).ToList();
        }
        /// <summary>
        /// Get all squads where the side does not match the side given
        /// </summary>
        /// <param name="side"></param>
        /// <returns></returns>
        public List<Squad> GetEnemySquads(int side)
        {
            return GetAllSquads().Where(squad => squad.Side != side).ToList();
        }
        public void AddToSquadsAwaitingHiveMindCommands(Squad squad)
        {
            _squadsAwaitingCommands.Enqueue(squad);
        }
        public Queue<Squad> GetSquadsAwaitingHiveMindCommands()
        {
            return _squadsAwaitingCommands;
        }
        public List<Squad> GetTargetedSquads(int side)
        {
            List<Squad> targetedSquads = new List<Squad>();
            _squads.Where((s) => s.Side == side).ToList().ForEach((s) =>
            {
                if (s.Command != null && s.Command.Enemy != null)
                {
                    targetedSquads.Add(s.Command.Enemy);
                }
            });
            return targetedSquads;
        }


        public List<StoredCommand> GetPastCommands()
        {
            return _pastCommands;
        }
        public void StoreCommands()
        {
            List<StoredCommand> completes = _pastCommands.Where((c) => c.IsHiveMindCommand && c.IsFinalized && !c.IsStored).ToList();
            List<StoredCommand> commands = new List<StoredCommand>();
            List<StoredCommand> shootingCommands = new List<StoredCommand>();
            List<StoredCommand> targetingCommands = new List<StoredCommand>();

            if (completes.Count > 0)
            {

                completes.ForEach((command) =>
                {
                    commands.Add(command);
                    if (command.ShootingStrategy != null)
                    {
                        shootingCommands.Add(command);

                    }
                    if (command.MatchupStrategy != null)
                    {
                        targetingCommands.Add(command);
                    }

                    command.IsStored = true;
                });

                ConfigData.Socket.SendRequest(new StoreCommandsRequest(new StoreCommands(commands, shootingCommands, targetingCommands),
                    ConfigData.StandardMaxTimeOnQueue));
                _pastCommands = _pastCommands.Where(c => !c.IsStored).ToList();
            }




        }

        public bool IsSideKilled(int side)
        {
            return GetShips(side).Count == 0;
        }

    }
}
