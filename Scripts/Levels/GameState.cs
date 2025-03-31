using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
//using System.Security.Policy;
using System.Text.RegularExpressions;
using System.Windows.Input;
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
        public HashSet<Projectile> Projectiles = new HashSet<Projectile>();
        public List<Ship> Ships = new List<Ship>();
        public List<Ship> ShipsToRelease = new List<Ship>();
        public Dictionary<long, Ship> ShipsById = new Dictionary<long, Ship>();
        public List<Squad> Squads = new List<Squad>();
        public List<Squad> SquadsToRelease = new List<Squad>();
        public Queue<Squad> SquadsAwaitingCommands = new Queue<Squad>();
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public List<Command> CommandsToRelease = new List<Command>();
        public List<Squad> SelectedSquads = new List<Squad>();
        public List<Obstacle> Obstacles = new List<Obstacle>();
        public List<CollisionAsteroid> AsteroidsToRelease = new List<CollisionAsteroid>();
        public List<MiningAsteroid> MiningAsteroidsToRelease = new List<MiningAsteroid>();

        public int UserCommands, AICommands;
        public bool IsPaused;
        public bool GameOver = false;
        public bool LevelEnded = false;
        public int[] InitialTsv = new int[] { 0, 0 };
        public List<SpottedShip>[] SpottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };
        public int[] OriginalSquadCounts = new int[] { 0, 0 };
        public Level Level;
        public Stage Stage;
        /// <summary>
        /// A hashset of all ships that a given side can see
        /// </summary>
        public HashSet<Ship>[] VisionCache = new HashSet<Ship>[] { new HashSet<Ship>(), new HashSet<Ship>() };
        /// <summary>
        /// A dictionary of every ship, (keyed by Id) that has a hashset of all the ships this ship has seen
        /// </summary>
        public Dictionary<long, HashSet<Ship>>[] HivemindShips = new Dictionary<long, HashSet<Ship>>[] { new Dictionary<long, HashSet<Ship>>(), new Dictionary<long, HashSet<Ship>>() };
        public List<ShipRemains> Deadbodies = new List<ShipRemains>();
        public HashSet<RocketExplosion> FireBargeExplosions = new HashSet<RocketExplosion>();
        public HashSet<MiningAsteroid> MiningAsteroids = new HashSet<MiningAsteroid>();
        public HashSet<Ship> MiningShips = new HashSet<Ship>();
        public bool HasWarpGates, IsFireBargeExploding, HasSelectedSquads, HasBeehives;
        public List<ShipDamageStatus>[] ShipDamageStatuses = new List<ShipDamageStatus>[] {new List<ShipDamageStatus>(), new List<ShipDamageStatus>() };
        public Dictionary<long, int> OutcomeIdToPastCommandIndex = new Dictionary<long, int>();
        //public bool[] HasMiningShips = new bool[2];

        public List<string> __Squads, __SquadsAwaitingCommands, __PastCommands, __Obstacles;
        public void UpdateDebugVariables()
        {
            __Squads = GetAllSquads().Select(s => s.ToString()).ToList();
            __SquadsAwaitingCommands = SquadsAwaitingCommands.Select(s => s.ToString()).ToList();
            __PastCommands = PastCommands.Select((c) => $"Command #{c.OutcomeId} - {c.CommandType} against {c.Enemy} ended with {c.Tsv}" +
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
            Deadbodies.Clear();
            FireBargeExplosions.Clear();
            MiningAsteroids.Clear();
            MiningShips.Clear();
            ShipDamageStatuses[0].Clear();
            ShipDamageStatuses[1].Clear();
            ShipsToRelease.Clear();
            SquadsToRelease.Clear();
            CommandsToRelease.Clear();
            AsteroidsToRelease.Clear();
            MiningAsteroidsToRelease.Clear();
            Projectiles.Clear();

        }

        private List<SpottedShip> _spottedShips;
        private int _maxExistingShips, _maxNewShips, _index, _shipIndex;
        private bool _duplicate;
        private Ship _spottedShip;
        public void AddSpottedShips(List<Ship> spottedShips, Ship spotter)
        {
            _spottedShips = SpottedShips[spotter.Side - 1];
            _maxExistingShips = spottedShips.Count;
            _maxNewShips = _spottedShips.Count;
            for (_index = 0; _index < _maxExistingShips; _index++)
            {

                _duplicate = false;
                _spottedShip = spottedShips[_index];
                for (_shipIndex = 0; _shipIndex < _maxNewShips && !_duplicate; _shipIndex++)
                {
                    _duplicate = _spottedShips[_shipIndex].Ship.Id == _spottedShip.Id;
                }
                _spottedShips.Add(new SpottedShip(_spottedShip, spotter.Id));
            }
        }
        /// <summary>
        /// If either there is a user and the user has mining ships and the level has mining asteroids, or there is no user and there are mining ships and mining asteroids
        /// </summary>
        /// <returns></returns>
        public bool CanShipsKeepMining()
        {
            return MiningShips.Count > 0 && MiningAsteroids.Count > 0;
        }

        public int AddUserCommand()
        {
            return UserCommands++;
        }
        /// <summary>
        /// Returns an incremented Id that is guarenteed unique for all other objects (that have Ids) in the entire stage
        /// </summary>
        /// <returns></returns>
        public int GetId()
        {
            return Stage.Pool.ItemCount++;
        }
        public void AddShip(Ship ship)
        {
            // Debug.Log($"{ship.name} has been added to the state");
            Ships.Add(ship);
            ShipsById.Add(ship.Id, ship);
        }
        public void AddSquad(Squad squad)
        {
            Squads.Add(squad);
            OriginalSquadCounts[squad.Side - 1]++;
        }
        public void RemoveSquad(Squad squad)
        {
            Squads.Remove(squad);
            SquadsToRelease.Add(squad);
        }
        public void AddProjectile(Projectile projectile)
        {
            Projectiles.Add(projectile);
        }
        public void RemoveProjectile(Projectile projectile)
        {
            Projectiles.Remove(projectile);
        }
        public void AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
        }
        public void RemoveShip(Ship ship)
        {
            Ships.Remove(ship);
            MiningShips.Remove(ship);
            ShipsById.Remove(ship.Id);
            ShipsToRelease.Add(ship);
        }
        public void RemoveObstacle(Obstacle obstacle)
        {
            Obstacles.Remove(obstacle);
        }
        public void AddCommand(Command command)
        {
            //_commands.Add(command);
            PastCommands.Add(new StoredCommand(command));
            OutcomeIdToPastCommandIndex.Add(command.OutcomeId, PastCommands.Count - 1);
            //Debug.Log($"Added Command {command} to past commands at index #{(PastCommands.Count - 1)}");
            AICommands++;
            Stage.__HivemindCommands++;
        }

        public void Release()
        {
            ShipsToRelease.ForEach((ship) =>
            {
                Stage.Pool.ReturnShipToPool(ship);
            });

            CommandsToRelease.ForEach((command) =>
            {
                Stage.Pool.ReturnCommandToPool(command);
            });

            SquadsToRelease.ForEach((squad) =>
            {
                Stage.Pool.ReturnSquadToPool(squad);
            });

            AsteroidsToRelease.ForEach(asteroid =>
            {
                Stage.Pool.ReturnCollisionAsteroidToPool(asteroid);
            });

            MiningAsteroidsToRelease.ForEach((miningAsteroid) =>
            {
                Stage.Pool.ReturnMiningAsteroidToPool(miningAsteroid);
            });
        }

        private ShipDamageStatus _shipDamageStatus;
        /// <summary>
        /// Finds the damage status entry for this ship in the side's list or creates it if it doesn't exist
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public ShipDamageStatus GetShipDamageStatus(int side, Ship potentialTargetShip)
        {
            _shipDamageStatus = null;
            if (ShipDamageStatuses[side - 1].Count > 0)
            {
                for (_index = 0; _index < ShipDamageStatuses[side - 1].Count; _index++)
                {
                    if (ShipDamageStatuses[side - 1][_index].Ship == potentialTargetShip)
                    {
                        _shipDamageStatus = ShipDamageStatuses[side - 1][_index];
                    }
                }
                //_shipDamageStatus = ShipDamageStatuses[side - 1].FirstOrDefault(s => !s.Ship.IsDead && s.Ship == potentialTargetShip);
            }

            if (_shipDamageStatus == null)
            {
                _shipDamageStatus = new ShipDamageStatus(potentialTargetShip);
                ShipDamageStatuses[side - 1].Add(_shipDamageStatus);
            }
            return _shipDamageStatus;
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
                sum.UnionWith(dictionary.Value.Where((ship) => !ship.IsDead));
                return sum;
            });
            
            return VisionCache[side - 1];
        }
        public void AddDeadBody(ShipRemains body)
        {
            Deadbodies.Add(body);
        }
        public HashSet<ConfigData.ShipTypes> GetHumanShipTypes()
        {
            return GetHumanShips().Select((s) => s.ShipType).ToHashSet();
        }
        public List<Ship> GetBeeShips()
        {
            return GetShips(ConfigData.Configuration.BeeSide);
        }
        public HashSet<ConfigData.ShipTypes> GetBeeShipTypes()
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
            return ShipsById.GetValueOrDefault(id);
        }

        /// <summary>
        /// If this is the user, returns all enemy squads, if this is the AI, returns all squads that have ships visible to the Hive Mind
        /// </summary>
        /// <param name="side"></param>
        /// <returns></returns>
        public List<Squad> GetSquadsVisibleToHiveMind(int side = 0)
        {
            if (side == ConfigData.Configuration.UserSide && Level.HasPlayer)
            {
                return GetEnemySquads(side);
            }
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
        public void SelectSquadsByShipType(ConfigData.ShipTypes type)
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
            return GetAllSquads().FirstOrDefault(squad => squad.Id == Id);
        }
        public List<Squad> GetAllSquads()
        {
            return Squads;
        }
        public List<Squad> GetSquadsBySide(int side)
        {
            return GetAllSquads().Where(squad => squad.Side == side && !squad.IsDead).ToList();
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
            //Debug.Log($"Adding {squad} to hive mind command queue");
            SquadsAwaitingCommands.Enqueue(squad);
        }
        public Queue<Squad> GetSquadsAwaitingHiveMindCommands()
        {
            return SquadsAwaitingCommands;
        }
        private List<Squad> _targetedSquads;
        public List<Squad> GetTargetedSquads(int side)
        {
            _targetedSquads = new List<Squad>();
            GetAllSquads().Where((s) => s.Side == side).ToList().ForEach((s) =>
            {
                if (s.HasCommand && s.GetCommand().HasEnemy && !s.GetCommand().EnemySquad.IsDead)
                {
                    _targetedSquads.Add(s.GetCommand().EnemySquad);
                }
            });
            return _targetedSquads;
        }


        private List<StoredCommand> _completes = new List<StoredCommand>();
        private List<StoredCommand> _commands = new List<StoredCommand>();
        private List<StoredCommand> _shootingCommands = new List<StoredCommand>();
        private List<StoredCommand> _targetingCommands = new List<StoredCommand>();
        /// <summary>
        /// Stores all the finalized hivemind commands with the server. 
        /// </summary>
        public void StoreCommands()
        {
            _completes = PastCommands.Where((c) => c.IsHiveMindCommand && c.IsFinalized).ToList();
            //Debug.Log($"_completes list: {_completes.Count} / {PastCommands.Count}");
            if (_completes.Count > 0)
            {

                _completes.ForEach((command) =>
                {
                    OutcomeIdToPastCommandIndex.Remove(command.OutcomeId);
                    _commands.Add(command);
                    if (command.ShootingStrategy != null)
                    {
                        _shootingCommands.Add(command);

                    }
                    else
                    {
                        Debug.LogError($"Stored command didn't have a shooting strategy");
                    }
                    if (command.MatchupStrategy != null)
                    {
                        _targetingCommands.Add(command);
                    }
                    else
                    {
                        Debug.LogError($"Stored command didn't have a matchup strategy");
                    }
                    Debug.Log($"Stored past command {command}");

                });

                ConfigData.Socket.SendRequest(new StoreCommandsRequest(new StoreCommands(_commands, _shootingCommands, _targetingCommands),
                    ConfigData.StandardMaxTimeOnQueue));
                //PastCommands = PastCommands.Where(c => !c.IsStored).ToList();
                PastCommands.Clear();
                OutcomeIdToPastCommandIndex.Clear();

                _commands.Clear();
                _shootingCommands.Clear();
                _targetingCommands.Clear();
            }




        }

        public bool IsSideKilled(int side)
        {
            return GetShips(side).Count == 0 || !GetShips(side).Where((s) => s.IsMobile).Any();
        }

    }
}
