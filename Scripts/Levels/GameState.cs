using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
//using System.Security.Policy;
using System.Text.RegularExpressions;
using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using UnityEngine;

namespace Assets.Scripts.Levels

{
    public class GameState : MonoBehaviour
    {
        public List<Ship> Ships = new List<Ship>();
        public List<Squad> Squads = new List<Squad>();
        public Queue<Squad> SquadsAwaitingCommands = new Queue<Squad>();
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public List<Squad> SelectedSquads = new List<Squad>();
        public List<Obstacle> Obstacles = new List<Obstacle>();

        public int EntityCount, UserCommands, AICommands;
        public bool IsPaused;
        public bool GameOver = false;
        public bool LevelEnded = false;
        public int[] InitialTsv = new int[] { 0, 0 };
        public List<SpottedShip>[] SpottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };
        public int[] OriginalSquadCounts = new int[] { 0, 0 };
        public Level Level;
        public Stage Stage;
        public HashSet<Ship>[] VisionCache = new HashSet<Ship>[] { new HashSet<Ship>(), new HashSet<Ship>() };
        public Dictionary<long, HashSet<Ship>>[] HivemindShips = new Dictionary<long, HashSet<Ship>>[] { new Dictionary<long, HashSet<Ship>>(), new Dictionary<long, HashSet<Ship>>() };
        public List<ShipRemains> Deadbodies = new List<ShipRemains>();
        public HashSet<RocketExplosion> FireBargeExplosions = new HashSet<RocketExplosion>();
        public HashSet<MiningAsteroid> MiningAsteroids = new HashSet<MiningAsteroid>();
        public HashSet<Ship> MiningShips = new HashSet<Ship>();
        public bool HasWarpGates, IsFireBargeExploding, HasSelectedSquads;
        public List<ShipDamageStatus>[] ShipDamageStatuses = new List<ShipDamageStatus>[] {new List<ShipDamageStatus>(), new List<ShipDamageStatus>() };
        //public bool[] HasMiningShips = new bool[2];

        public List<String> __Squads, __SquadsAwaitingCommands, __PastCommands, __Obstacles;
        public void UpdateDebugVariables()
        {
            __Squads = Squads.Select(s => s.ToString()).ToList();
            __SquadsAwaitingCommands = SquadsAwaitingCommands.Select(s => s.ToString()).ToList();
            __PastCommands = PastCommands.Select((c) => $"Command #{c.OutcomeId} - {c.Strategy.Name} against {c.Enemy} ended with {c.Tsv}" +
            $" TSV due to \"{c.FinalizationCause}\" and took {c.Age} ticks").ToList();

            __Obstacles = Obstacles.Select((o) => $"{o.Name} at {o.GetPosition()} with {o.Health} health").ToList();
        }

        public void Setup(Level level)
        {
            Level = level;
            Stage = Level.Stage;
            // Debug.Log("Game State has been setup");
        }
        public void ResetState()
        {
            Ships.Clear();
            Squads.Clear();
            SquadsAwaitingCommands.Clear();
            PastCommands.Clear();
            SelectedSquads.Clear();
            Obstacles.Clear();
            SpottedShips[0].Clear();
            SpottedShips[1].Clear();
            InitialTsv = new int[] { 0, 0 };
            OriginalSquadCounts = new int[] { 0, 0 };
            HivemindShips = new Dictionary<long, HashSet<Ship>>[] { new Dictionary<long, HashSet<Ship>>(), new Dictionary<long, HashSet<Ship>>() };
            VisionCache = new HashSet<Ship>[] { new HashSet<Ship>(), new HashSet<Ship>() };
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
        public bool CanUserKeepMining()
        {
            return MiningShips.Count > 0 && MiningAsteroids.Count > 0;
        }

        public int AddUserCommand()
        {
            return UserCommands++;
        }
        public int GetId()
        {
            return EntityCount++;
        }
        public void AddShip(Ship ship)
        {
            // Debug.Log($"{ship.name} has been added to the state");
            Ships.Add(ship);
        }
        public void AddSquad(Squad squad)
        {
            Squads.Add(squad);
            OriginalSquadCounts[squad.Side - 1]++;
        }
        public void AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
        }
        public void RemoveShip(Ship ship)
        {
            Ships.Remove(ship);
            MiningShips.Remove(ship);
        }
        public void RemoveObstacle(Obstacle obstacle)
        {
            Obstacles.Remove(obstacle);
        }
        public void AddCommand(Command command)
        {
            //_commands.Add(command);
            PastCommands.Add(new StoredCommand(command));
            AICommands++;
            ConfigData.__HivemindCommands++;
        }

        /// <summary>
        /// Finds the damage status entry for this ship in the side's list or creates it if it doesn't exist
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public ShipDamageStatus GetShipDamageStatus(int side, Ship potentialTargetShip)
        {
            ShipDamageStatus shipDamageStatus = null;
            if (ShipDamageStatuses[side - 1].Count > 0)
            {
                shipDamageStatus = ShipDamageStatuses[side - 1].FirstOrDefault(s => s != null && s.Ship != null && s.Ship.Equals(potentialTargetShip));
            }

            if (shipDamageStatus == null)
            {
                shipDamageStatus = new ShipDamageStatus(potentialTargetShip);
                ShipDamageStatuses[side - 1].Add(shipDamageStatus);
            }
            return shipDamageStatus;
        }
        public List<Obstacle> GetObstacles()
        {
            return Obstacles;
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
                1 => Ships.Where(ship => ship.Side == 1).ToList(),
                2 => Ships.Where(ship => ship.Side == 2).ToList(),
                _ => Ships
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
        public void AddDeadBody(ShipRemains body)
        {
            Deadbodies.Add(body);
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
            return Ships.Where(ship => ship.Side != side).ToList();
        }
        public Ship GetShipById(long id)
        {
            return Ships.FirstOrDefault(ship => ship.Id == id);
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
            return SelectedSquads.Where((squad) => squad != null).ToList();
        }
        public void AddSelectedSquad(Squad squad)
        {
            if (squad != null && squad.IsUserControlled && !SelectedSquads.Contains(squad))
            {
                SelectedSquads.Add(squad);
                squad.IsSelected = true;
                squad.MoveSquadBox();
                Stage.Menus.ActionBox.SetupForSquad();
                squad.GetShips().ForEach((ship) =>
                {
                    if (ship.HasTargetCoordinates)
                    {
                        ship.MovementMarker.SetActive(true);
                    }
                });
                if (squad.HasSquadTab)
                {
                    squad.SquadTab.ShowSelected();
                }
                HasSelectedSquads = true;
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
        public void SelectSquadsByShipType(string type)
        {
            ClearSelectedSquads();
            foreach (Squad squad in GetSquadsBySide(ConfigData.Configuration.UserSide).Where((squad) => squad.GetShips().Any((ship) => ship.ShipType == type)))
            {
                AddSelectedSquad(squad);
            }
        }
        public void ClearSelectedSquads()
        {
            //SelectedSquads.ForEach((squad) =>
            //{
            //    DeselectSquad(squad);
            //});
            while (SelectedSquads.Count > 0)
            {
                DeselectSquad(SelectedSquads[0]);
            }
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
        public void DeselectSquad(Squad squad)
        {
            squad.DeactivateSquadBox();
            squad.IsSelected = false;
            squad.GetShips().ForEach((ship) =>
            {
                if (ship.IsMobile)
                {
                    ship.MovementMarker.SetActive(false);
                }
            });
            if (squad.HasSquadTab)
            {
                squad.SquadTab.HideSelected();
            }
            SelectedSquads.Remove(squad);

            if (SelectedSquads.Count == 0)
            {
                HasSelectedSquads = false;
                if (!Level.Stage.IsTraining)
                {
                    Stage.Menus.ActionBox.Hide();
                }
            }
            else if (!Level.Stage.IsTraining)
            {
                Stage.Menus.ActionBox.SetupForSquad();
            }
        }
        public Squad GetSquadByNumber(int side, int squadNumber)
        {
            return GetSquadsBySide(side).FirstOrDefault(squad => squad.SquadNumber == squadNumber);
        }
        public Squad GetSquadById(long Id)
        {
            return Squads.FirstOrDefault(squad => squad.Id == Id);
        }
        public List<Squad> GetAllSquads()
        {
            return Squads.Where(squad => squad != null && !squad.IsDead).ToList();
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
            SquadsAwaitingCommands.Enqueue(squad);
        }
        public Queue<Squad> GetSquadsAwaitingHiveMindCommands()
        {
            return SquadsAwaitingCommands;
        }
        public List<Squad> GetTargetedSquads(int side)
        {
            List<Squad> targetedSquads = new List<Squad>();
            Squads.Where((s) => s.Side == side).ToList().ForEach((s) =>
            {
                if (s.HasCommand && s.Command.EnemySquad != null)
                {
                    targetedSquads.Add(s.Command.EnemySquad);
                }
            });
            return targetedSquads;
        }


        public List<StoredCommand> GetPastCommands()
        {
            return PastCommands;
        }
        public void StoreCommands()
        {
            List<StoredCommand> completes = PastCommands.Where((c) => c.IsHiveMindCommand && c.IsFinalized && !c.IsStored).ToList();
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
                PastCommands = PastCommands.Where(c => !c.IsStored).ToList();
            }




        }

        public bool IsSideKilled(int side)
        {
            return GetShips(side).Count == 0 || !GetShips(side).Where((s) => s.IsMobile).Any();
        }

    }
}
