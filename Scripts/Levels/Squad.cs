using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Squad : MonoBehaviour
    {
        public Level Level;
        public Stage Stage;
        public int Side, SquadNumber, OpponentId, Id;
        /// <summary>
        /// The Id of this squad relative to the stage. Guarenteed unique for this stage. Not the same as the saved squad Id
        /// </summary>
        public int ItemId;
        public long Age;
        public ConfigData.SquadTypes SquadType;
        public Command Command;
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        /// <summary>
        /// The matchup strategy belongs to the squad and not the command because it is used to determine the command by making the matchup
        /// </summary>
        public MatchupStrategy MatchupStrategy = new MatchupStrategy(); 
        public HashSet<ConfigData.CommandTypes> BannedStrats = new HashSet<ConfigData.CommandTypes>();
        public string Status;
        public string Name;
        public Color Color;
        public Color SquadBoxColor;
        public SavedSquad SavedSquad;
        public GameObject SquadBox;
        public SquadTab SquadTab;
        public bool HasMovedBox, IsMatchingSpeed, IsImmobile, CeaseFire, HasAddedShips, IsShowingRanges, IsGrowingSquad, HasCustomColor, HasSquadTab, HasSquadBox;
        public Vector2 Destination;
        /// <summary>
        /// A squad can be dead for one frame before it is destroyed. It's important to check for the death of a squad on anything run by a timer outside of the squad object
        /// </summary>
        public bool IsDead;
        /// <summary>
        /// Is this squad is selected by the user?
        /// </summary>
        public bool IsSelected;
        /// <summary>
        /// If this squad belongs to the user side and there is a player
        /// </summary>
        public bool IsUserControlled;
        /// <summary>
        /// If the side is the AI side or the level has no players
        /// </summary>
        public bool IsHiveMindControlled;
        public bool IsCarrierSquad;
        public bool HasCommand;
        public float CurrentSpeed;

        private List<Ship> _ships = new List<Ship>();
        private bool _shouldChase = false;
        private ConfigData.ShootingStrategyTypes _chosenShootingStrategy; // there is a shooting strategy attached to the squad because users attach shooting strategies to the squad whereas the AI attaches them to the command

        public int LastKilled => GetShips().Max(s => s.LastKilled);
        public int DamageDone => GetShips().Sum(s => s.FleetShip.DamageDone);
        public int Health => GetShips().Sum(s => s.Health);
        public float Firepower => GetShips().Sum(s => s.Firepower);
        public int MaxRange => GetShips().Max(s => s.MaxRange);
        public int MaxSight => GetShips().Max(s => s.Sight);
        public float TotalSpeed => GetShips().Sum(s => s.Speed);
        public float MaxSpeed => GetShips().Max(s => s.Speed);
        public int Tsv => GetShips().Sum(s => s.Tsv);
        public float SlowestSpeed => GetShips().Min(s => s.Speed);
        public bool HasEnemy => HasCommand && Command.HasEnemy;
        public bool IsAttacking => HasCommand && Command.IsAttacking;
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide ? Level.Map.UserStartingPosition : Level.Map.AIStartingPosition;
        public bool IsDefenseless => GetShips().All((s) => s.Firepower == 0);
        public bool HasMiningShips => GetShips().Any((s) => s.IsMiningShip);
        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();
        public bool HasOnlyStrikers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker);
        public bool HasOnlyBombers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker || s.ShipType == ConfigData.ShipTypes.YellowJacket || 
        s.ShipType == ConfigData.ShipTypes.FireBarge || s.ShipType == ConfigData.ShipTypes.Barge);
        public bool HasOnlyBarges => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Barge);
        public bool HasOnlyWarpGates => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.WarpGate);

        /// <summary>
        /// Whether or not the squad's ships have target coordinates. If they do, it hasn't reached the destination
        /// </summary>
        public bool HasReachedDestination => GetShips().All((s) => s.HasReachedDestination);
        public bool HasDestination => GetShips().Any((s) => s.HasTargetCoordinates);
        /// <summary>
        /// A squad is in combat if any of its ships are in combat. This is used for Matchup strategies that target squads that are in combat.
        /// </summary>
        public bool InCombat => GetShips().Any((s) => s.InCombat);


        public List<Ship> __Ships;

        // Setup methods
        public virtual void ClearData()
        {
            Command = null;
            HasCommand = false;
            PastCommands.Clear();
            BannedStrats.Clear();
            Status = "idle";
            HasMovedBox = false;
            IsImmobile = false;
            HasAddedShips = false;
            IsShowingRanges = false;
            HasSquadTab = false;
            HasSquadBox = false;
            IsGrowingSquad = false;
            HasCustomColor = false;
            _ships.Clear();
            _shouldChase = false;
            Destination = Vector2.zero;
            IsDead = false;
            CurrentSpeed = 0;
            MatchupStrategy.Kill();

        }
        public virtual void Create(Stage stage)
        {
            SquadType = ConfigData.SquadTypes.Squad;
            Stage = stage;
            IsDead = true;
        }
        public void Setup(Level level, SavedSquad savedSquad, ConfigData.ShootingStrategyTypes shootingStrategy, bool ceaseFire, bool isMatchingSpeed, bool shouldChase,
            int id, int side, int squadNumber, string name, Color color)
        {
            ClearData();
            Level = level;
            SavedSquad = savedSquad;
            Id = id;
            Side = side;
            Name = name;
            Color = color;
            SquadNumber = squadNumber;
            IsMatchingSpeed = isMatchingSpeed;
            ItemId = Level.State.GetId();
            CeaseFire = ceaseFire;
            _shouldChase = shouldChase;
            SetShootingStrategy(shootingStrategy);
            SetOpponent();

            IsUserControlled = Side == ConfigData.Configuration.UserSide && Level.HasPlayer; 
            if (!IsUserControlled)
            {
                IsHiveMindControlled = true;
            }

            if (Color != ConfigData.UnsetColor && IsUserControlled)
            {
                HasCustomColor = true;
                SquadBoxColor = new Color(Color.r, Color.g, Color.b, ConfigData.GetUIColor("squadbox-default-color").a);
            }
            else
            {
                SquadBoxColor = ConfigData.GetUIColor("squadbox-default-color");
            }
            
            if (IsHiveMindControlled && !IsImmobile)
            {
                AddToCommandList();
            }
            else
            {
                //Debug.Log($"Squad: {Name}, Side: {Side}, HiveMindControlled: {IsHiveMindControlled}, Has Brain: {HasBrain}");
            }

            transform.parent = Level.Map.transform;

            if (IsUserControlled)
            {
                InvokeRepeating(nameof(CheckChase), 5, 1);
            }
            if (Stage.FullCeaseFire || Side == ConfigData.Configuration.AISide && Stage.MakeEnemyCeaseFire)
            {
                CeaseFire = true;
            }
        }

        private void SetOpponent()
        {
            if (Side == ConfigData.Configuration.AISide)
            { // ai side
                if (!Stage.IsTraining)
                {
                    OpponentId = ConfigData.GetUserId();
                }
                else
                {
                    OpponentId = 1;
                }
            }
            else if (Side == ConfigData.Configuration.UserSide)
            { // user side
                OpponentId = 0;

                if (!Stage.IsTraining)
                {
                    if (!HasSquadBox)
                    {
                        SquadBox = Instantiate(Stage.Prefabs.SquadBoxPrefab, Vector2.zero, Quaternion.identity);
                        HasSquadBox = true;
                    }
                    SquadBox.transform.parent = Level.Map.transform;
                    SquadBox.SetActive(false);
                    SquadBox.name = $"{Name} - Squadbox"; // [note] [testing] Only used for testing
                }
            }
        }

        /* When squads are setup, they are position centrally like a line formation, first in the center, second on the right, third on the left, fourth on the right, and so on
         * The squad is set to the center point and all the ships are positioned based off their original offset
         */
        public void SetStartingPosition(Vector2 position)
        {
            //float largestShipSize = ConfigData.ShipSizeFactor.GetValueOrDefault(
            //    GetShips().OrderByDescending((s) => ConfigData.ShipSizeFactor.GetValueOrDefault(s.ShipType)).ToList().First().ShipType
            //    );
            GetShips().ForEach((ship) =>
            {
                // The size factor (1-16)
                //Vector2 sizeFactor = (largestShipSize / ConfigData.DragIconSize) * ConfigData.WorldUnitScaleFactor;


                // trying to place ships on the map according to where they were in the squad maker
                // Option 1: Convert the squadmaker coordinates directly to map coordinates

                //Debug.Log($"Ship: {ship.Name} Position: {position}, Offset from Center: {ship.OffsetFromCenter}");

                Vector2 adjustment = ship.OffsetFromCenter;

                if (ship.ShipType == ConfigData.ShipTypes.Queen)
                {
                    adjustment *= new Vector2(2.75f, 2); // Need larger spacing between the Queen(s) because it's so large
                }
                else if (ship.ShipType == ConfigData.ShipTypes.Bumblebee)
                {
                    adjustment *= 1.2f;
                }
                else if (ship.ShipType == ConfigData.ShipTypes.Barge || ship.ShipType == ConfigData.ShipTypes.FireBarge || ship.ShipType == ConfigData.ShipTypes.WarpGate)
                {
                    adjustment *= new Vector2(1.4f, 1);
                }

                //Debug.Log($"Sizefactor for {ship.Name}: {sizeFactor}");
                //ship.transform.localPosition = Level.ForceBounds((position.x + adjustment.x), (position.y + adjustment.y));
                ship.transform.localPosition = new Vector2(position.x + adjustment.x, position.y + adjustment.y);
                //Debug.Log($"Local starting position for {ship.Name}: {ship.transform.localPosition}");

            });

        }
        public void SetSquadTab()
        {
            if (IsUserControlled && SquadNumber <= 10)
            {
                Debug.Log($"Stage: {Stage}, SquadNumber: {SquadNumber}");
                SquadTab = Stage.SquadTabs[SquadNumber - 1];
                HasSquadTab = true;
                if (HasCustomColor)
                {
                    SquadTab.SetColor(Color);
                }
                SquadTab.ShowTab();
            }
        }
        public void NameSquadShips()
        {
            foreach (Ship ship in GetShips())
            {
                ship.SetSquadName();
            }
        }
        protected void Update()
        {

            if (!Level.State.IsPaused)
            {
                Age++;
                if (HasCommand)
                {
                    Command.Age++;
                }
            }

            // Debug.Log($"{name} ship has lived for {tickLife} ticks and {ShowLifeTime()} seconds with {GetHealth()} health");
        }
        public void FixedUpdate()
        {
            if (IsUserControlled && !Level.State.IsPaused)
            {
                HasMovedBox = false;
            }
            //UpdateTestProperties();
        }
        public void SetOffsets()
        {
            Vector2 center = GetCenterPoint();
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.OffsetFromCenter = new Vector2(ship.GetX() - center.x, ship.GetY() - center.y);
            }
        }


        // Movement methods
        public void Move(Vector2 destination)
        {
            //if (IsAttacking)
            //{
            //    GetShips().ForEach((Ship ship) =>
            //    {
            //        Vector2 offset = ship.OffsetFromCenter;
            //        offset.x = Mathf.Clamp(offset.x, 2, ship.Range);
            //        offset.y = Mathf.Clamp(offset.y, 2, ship.Range);
            //        float x = Mathf.Clamp((destination.x + offset.x), Level.MinX, Level.MaxX);
            //        float y = Mathf.Clamp((destination.y + offset.y), Level.MinY, Level.MaxY);
            //        ship.TargetCoordinates = new Vector2(x, y);
            //    });
            //}
            //else
            //{
            //    GetShips().ForEach((Ship ship) =>
            //    {
            //        float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
            //        float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);
            //        ship.TargetCoordinates = new Vector2(x, y);
            //    });
            //}
            if (Level.Stage.DoesUserHaveController && IsSelected)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
            //float start = Time.realtimeSinceStartup;
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                //float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
                //float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);
                if (ship.IsMobile)
                {
                    ship.MoveToPoint(destination + ship.OffsetFromCenter);
                }
            }
            Destination = destination;
            //float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            //Debug.Log($"It took {Math.Round(end, 2)} ms to set {Name} moving. The average was {Math.Round(end / ships.Count, 2)}ms");

        }
        public void MatchSpeed(float speed = 0)
        {
            IsMatchingSpeed = true;
            if (speed > 0)
            {
                SetSquadSpeed(speed);

            }
            else
            {
                SetSquadSpeed(SlowestSpeed);
            }
        }
        public void UnmatchSpeed()
        {
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.SetCurrentSpeed(ship.Speed);
            }
            IsMatchingSpeed = false;
        }
        public void SetSquadSpeed(float speed)
        {
            CurrentSpeed = speed;
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.SetCurrentSpeed(speed);
            }
        }
        public void SetChase(bool chase)
        {
            _shouldChase = chase;
        }
        public bool IsChasing()
        {
            return HasCommand && Command.CommandType == ConfigData.CommandTypes.Aggressive && _shouldChase;
        }
        public void StopChasing()
        {
            if (HasCommand && Command.CommandType == ConfigData.CommandTypes.Aggressive)
            {
                Command.SetFinalize("Stopped Chasing");
            }
            SetChase(false);
        }
        public bool ShouldChase()
        {
            return _shouldChase;
        }



        // Combat methods
        private void CheckChase()
        {
            //Debug.Log($"Checking if {Name} should chase.");
            if (_shouldChase && Command?.CommandType != ConfigData.CommandTypes.Aggressive)
            {
                Squad closestSquad = GetClosestEnemySquad();
                //Debug.Log($"The closest Enemy to {Name} is {closestSquad.Name}");
                if (CanSeeSquad(closestSquad))
                {
                    //Debug.Log($"Initiating chase by {Name} against {closestSquad.Name}.");
                    UserAggressive(closestSquad);
                }
            }
        }
        public void Kill(bool endKill = false)
        {
            //Debug.Log($"Killing squad {Name}");
            if (!IsDead)
            {
                IsDead = true;


                if (!endKill)
                {
                    if (HasCommand)
                    {
                        Command.SquadKilled();
                    }

                    if (IsUserControlled)
                    {
                        DeactivateSquadBox();
                    }

                    if (Level.State.IsSideKilled(Side))
                    {

                        Level.State.GameOver = true;
                    }
                    else
                    {
                        if (Level.State.GetSelectedSquads().Count == 0)
                        {
                            Level.State.SelectSquad(Level.State.GetSquadsBySide(Side).First());
                        }
                    }
                }

                if (IsUserControlled)
                {
                    if (HasSquadTab)
                    {
                        SquadTab.DisableTab();
                    }
                    SquadBox.gameObject.SetActive(false);
                    Level.State.DeselectSquad(this);
                }
                Stage.Pool.ReturnSquadToPool(this);
            }
            

        }
        public Squad GetClosestEnemySquad()
        {
            // Debug.Log($"Number of enemy squads {squads.Count}, {_Level.State.GetSquads()}");
            return Level.State.GetEnemySquads(Side).OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public Squad GetClosestValidFriendlySquad()
        {
            List<Squad> squads = Level.State.GetSquadsBySide(Side).Where(squad => squad != this && (!squad.HasCommand || squad.Command.CommandType != ConfigData.CommandTypes.ClosestFriendly)).ToList();
            return squads.OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public Squad GetEnemy()
        {
            if (Command != null)
            {
                return Command.EnemySquad;
            }
            else
            {
                if (IsUserControlled)
                {
                    return GetClosestEnemySquad();
                }
                return null;
            }
        }
        /// <summary>
        /// Gets all ships in the level that are on the opposing side
        /// </summary>
        /// <returns></returns>
        public List<Ship> GetEnemyShips()
        {
            return Level.State.GetShipsVisibleToHiveMind(Side).ToList();
        }
        public List<Ship> GetFriendlyShips()
        {
            return Level.State.GetShips(Side);
        }
        public List<Ship> GetPotentialEnemies(Squad target)
        {
            
            List<Ship> potentialEnemies = GetEnemyShips();
            List<Ship> enemies = potentialEnemies.Where((s) => s.Squad == target).ToList();

            foreach (Ship potentialEnemy in  potentialEnemies)
            {
                if (!potentialEnemy.Squad == target)
                {
                    if (potentialEnemy.IsAnySquadShipWithinRange(target)) // if any ship in the target squad is within the range of another of its allies (the potential enemy)
                    {
                        enemies.Add(potentialEnemy);
                    }
                    else if (potentialEnemy.Squad.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this)) // if the squad is within the range of the potential enemy
                    {
                        enemies.Add(potentialEnemy);
                    }
                }
            }

            return enemies;
        }
        public List<Ship> GetPotentialAllies(Squad target)
        {
            List<Ship> allies = GetShips().ToList(); // the ToList() is very important to prevent this list from modifying the main squad list
            List<Ship> potentialAllies = GetFriendlyShips();

            foreach(Ship potentialAlly in potentialAllies)
            {
                if (this != potentialAlly.Squad)
                {
                    if (potentialAlly.IsAnySquadShipWithinRange(target)) // if any ship in the target squad is within the range of another of its allies
                    {
                        allies.Add(potentialAlly);
                    }
                }
            }

            return allies;
        }


        // Command and control methods
        public void AddToCommandList()
        {          
            Level.State.AddToSquadsAwaitingHiveMindCommands(this);
        }
        public void MakeMatchupStrat()
        {
            // Can't get any invisible ship types and start by blocking the visible ship types too
            HashSet<ConfigData.ShipTypes> banned = ConfigData.Configuration.AllShipTypes.ToHashSet();

            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.BeeSide)
            {
                // if you're the bees you can only get available human ship types
                HashSet<ConfigData.ShipTypes> enemyShips = Level.State.GetHumanShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                // if you're the humans you can only get available bee ship types
                HashSet<ConfigData.ShipTypes> enemyShips = Level.State.GetBeeShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            
            string[] bannedTypes = banned.Select((ship) => $"Type {(Utilities.ConvertShipTypeToCharacter[ship])}").ToArray();

            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(new GetMatchupStrategy(AddToMatchup(GetShips()), OpponentId, bannedTypes),
                this, Level, ConfigData.StandardMaxTimeOnQueue));
        }
        public static string AddToMatchup(List<Ship> ships)
        {
            //string unsorted = "";
            //StringBuilder stringBuilder = new StringBuilder();
            char[] letters = ships.Select(s => Utilities.ConvertShipTypeLetterToCharacter[s.ShipTypeLetter]).ToArray();
            //ships.ForEach((ship) =>
            //{
            //    //unsorted += ship.ShipTypeLetter;
            //    //stringBuilder.Append(ship.ShipTypeLetter);
            //    letters.Add(ship.ShipTypeLetter.First());
            //});


            
            Array.Sort(letters);
            //Debug.Log(new string(letters));
            return new string(letters);
        }
        public void MakeMatchupAndGetCommand(Squad enemy = null)
        {
            string matchup = "";
            if (Level.Stage.OverrideStrats.Count > 0) // [debug]
            {
                BannedStrats.UnionWith(ConfigData.TypesOfCommands);
                BannedStrats = BannedStrats.Except(Level.Stage.OverrideStrats).ToHashSet();

                if (Level.Stage.OverrideStrats.Contains(ConfigData.CommandTypes.Scouting) && Level.State.GetShipsVisibleToHiveMind(Side).Count > 0 && Level.Stage.OverrideStrats.Count > 1 && !IsDefenseless)
                {
                    BannedStrats.Add(ConfigData.CommandTypes.Scouting);
                }
            }
            HashSet<ConfigData.CommandTypes> banned = BannedStrats.ToHashSet(); // the ToHashSet is important to prevent modification of the original set

            if (enemy != null)
            {
                List<Ship> enemies = GetPotentialEnemies(enemy);
                List<Ship> allies = GetPotentialAllies(enemy);

                /*
                Determines whether or not the squad is at the "walls"
                 */

                int atTheWalls = 0;
                int distance = 15;
                Vector2 position = GetPosition();
                if (position.x < (Level.Map.SpriteRenderer.bounds.min.x + distance) || position.x > (Level.Map.SpriteRenderer.bounds.max.x - distance)) // check if it's at the sides
                {
                    atTheWalls = 1;
                    if (position.y < (Level.Map.SpriteRenderer.bounds.min.y + distance) || position.y > (Level.Map.SpriteRenderer.bounds.max.y - distance))
                    {
                        atTheWalls = 2;
                    }
                }
                else if (position.y < (Level.Map.SpriteRenderer.bounds.min.y + distance) || position.y > (Level.Map.SpriteRenderer.bounds.max.y - distance))
                {
                    atTheWalls = 1;
                }

                /*
                This is the calculation for the enemy's current average percentage of health for each ship and then the same for the allies, and then compares the allies to the enemies
                 */

                int comparativeHealth = (int)Math.Round((Ship.GetAverageHealthPercent(allies) / Ship.GetAverageHealthPercent(enemies)) * 100);

                /*
                 comparativeHealth
                 < 50 0
                 50 - 85 1
                 85 - 115 2
                 115 - 165 3
                 165+ 4
                 */

                if (comparativeHealth < 50)
                {
                    comparativeHealth = 0;
                }
                else if (comparativeHealth < 85)
                {
                    comparativeHealth = 1;
                }
                else if (comparativeHealth < 115)
                {
                    comparativeHealth = 2;
                }
                else if (comparativeHealth < 165)
                {
                    comparativeHealth = 3;
                }
                else
                {
                    comparativeHealth = 4;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append(AddToMatchup(allies));
                sb.Append("|");
                sb.Append(AddToMatchup(enemies));
                sb.Append("|");
                sb.Append((enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this) ? 1 : 0));
                sb.Append("|");
                sb.Append(comparativeHealth);
                sb.Append("|");
                sb.Append(atTheWalls);

                matchup = sb.ToString();
            }
            else
            {
                banned.Add(ConfigData.CommandTypes.Aggressive);
                banned.Add(ConfigData.CommandTypes.Retreat);
                banned.Add(ConfigData.CommandTypes.CircleSquad);
                banned.Add(ConfigData.CommandTypes.RightSwipe);
                banned.Add(ConfigData.CommandTypes.LeftSwipe);
                banned.Add(ConfigData.CommandTypes.InAndOut);
            }

            int closestFriendlySquadCount = Level.State.GetSquadsBySide(Side).Where((squad) => squad?.Command?.Strategy.CommandType == ConfigData.CommandTypes.ClosestFriendly).Count();
            int friendlySquadCount = Level.State.GetSquadsBySide(Side).Count;
            if (friendlySquadCount - 1  <= friendlySquadCount)
            {
                banned.Add(ConfigData.CommandTypes.ClosestFriendly);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.Mining) && (!HasMiningShips || !Level.ActivateMining))
            {
                BannedStrats.Add(ConfigData.CommandTypes.Mining);
                banned.Add(ConfigData.CommandTypes.Mining);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.FullRetreat) && (Side != ConfigData.Configuration.HumanSide || !Level.State.HasWarpGates || HasOnlyWarpGates))
            {
                BannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
                banned.Add(ConfigData.CommandTypes.FullRetreat);
            }

            //if (HasOnlyYellowJackets)
            //{
            //    Debug.Log($"Trying to get a command for {Name} against {enemy?.Name}");
            //    for (int i = 0; i < banned.Count; i++)
            //    {
            //        Debug.Log($"banned #{i} is {banned.ElementAt(i)}");
            //    }
            //}


            ConfigData.Socket.SendRequest(new CommandRequest(new GetStrategy(matchup, OpponentId, banned.Select(b => Utilities.ConvertCommandTypeToName[b]).ToArray()),
                this, enemy, Level, matchup, ConfigData.StandardMaxTimeOnQueue));


        }
        public void ClearTargets()
        {
            GetShips().ForEach(ship =>
            {
                ship.ClearTargets();
            });
        }
        public void SetShootingStrategy(ConfigData.ShootingStrategyTypes strategy)
        {
            _chosenShootingStrategy = strategy;
            if (HasCommand && Command.HasShootingStrategy)
            {
                Command.ShootingStrategy.ShootingStrategyType = strategy;
            }
            GetShips().ForEach((ship) =>
            {
                ship.ShootingStrategy = _chosenShootingStrategy;
            });
        }
        public ConfigData.ShootingStrategyTypes GetShootingStrategy()
        {
            return _chosenShootingStrategy;
        }
        public ConfigData.CommandTypes GetCommandStrategy()
        {
            if (HasCommand && Command.HasStrategy)
            {
                return Command.Strategy.CommandType;
            }
            return ConfigData.CommandTypes.Uninitialized;
        }
        public void UserGuard(Squad squad)
        {

            MakeUserCommand(ConfigData.CommandTypes.Guard, null);
            ((Guard)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, true, squad);
            
            if (Level.Stage.DoesUserHaveController)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            MakeUserCommand(ConfigData.CommandTypes.Patrol, null);
            ((Patrol)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, true, topLeft, bottomRight);
            if (Level.Stage.DoesUserHaveController)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }


        }
        public void UserMining(MiningAsteroid miningAsteroid)
        {
            if (HasMiningShips)
            {
                MakeUserCommand(ConfigData.CommandTypes.Mining, null);
                ((Mining)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, true, miningAsteroid);
            }
            else
            {
                FinalizeUserCommand();
                Move(miningAsteroid.GetPosition());
            }

        }
        public void UserFullRetreat(WarpGate warpGate)
        {
            MakeUserCommand(ConfigData.CommandTypes.FullRetreat, null);
            ((FullRetreat)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, true, warpGate);

        }
        public void UserAggressive(Squad enemy)
        {
            if (HasOnlyBombers)
            {
                UserBombingRun(enemy);
                return;
            }
            MakeUserCommand(ConfigData.CommandTypes.Aggressive, enemy);
            ((Aggressive)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, false);

        }
        public void UserBombingRun(Squad enemy)
        {
            //Debug.Log($"Creating \"Bombing Run\" command for {Name} against {enemy.Name}");
            MakeUserCommand(ConfigData.CommandTypes.BombingRun, enemy);
            ((BombingRun)Command).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, false);

        }
        public void MakeUserCommand(ConfigData.CommandTypes command, Squad enemy)
        {
            //Debug.Log($"{Name} now has command against {enemy.Name}");
            FinalizeUserCommand();

            switch (command)
            {
                case ConfigData.CommandTypes.Aggressive:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                    break;
                case ConfigData.CommandTypes.BombingRun:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                    break;
                //case ConfigData.CommandTypes.Charge:
                //    Command = gameObject.AddComponent<Charge>();
                //    break;
                case ConfigData.CommandTypes.Guard:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Guard);
                    break;
                case ConfigData.CommandTypes.Patrol:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Patrol);
                    break;
                case ConfigData.CommandTypes.Mining:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Mining);
                    break;
                case ConfigData.CommandTypes.FullRetreat:
                    Command = Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.FullRetreat);
                    break;
                default:
                    Debug.LogError($"Invalid command {command} issued to user squad");
                    break;
            }


            Command.Setup(this, false, enemy, null);

        }
        public void FinalizeUserCommand()
        {
            if (HasCommand)
            {
                //Debug.Log($"Finalizing command for {Name}");
                //if (Command.Type != "Charge" || !((Charge)Command).IsCharging)
                //{
                //    if (Command.Type == "Guard")
                //    {
                //        UnmatchSpeed();
                //        ((Guard)Command).GetGuardingSquads().ForEach((squad) =>
                //        {
                //            ((Guard)squad.Command).OtherGuardSquads.Remove(this);
                //        });
                //    }
                //    Command.SetFinalize("New command given");
                //    return true;
                //}
                //else
                //{
                //    Debug.Log($"Can't finalize command for {Name}, the squad is charging");
                //}
                if (Command.CommandType == ConfigData.CommandTypes.Guard)
                {
                    UnmatchSpeed();
                    ((Guard)Command).GetGuardingSquads().ForEach((squad) =>
                    {
                        ((Guard)squad.Command).OtherGuardSquads.Remove(this);
                    });
                }
                Command.SetFinalize("New command given");
                
            }
        }
        public MiningAsteroid GetNearestMiningAsteroid()
        {
            return Level.State.MiningAsteroids.OrderBy((o) => DistanceToPoint(o.GetPosition())).FirstOrDefault();
        }


        // ship list methods
        public List<Ship> GetShips()
        {
            return _ships;
        }
        public void AddShip(Ship ship)
        {
            //Debug.Log($"Adding {ship.Name} to Squad {Name}");
            _ships.Add(ship);
            if (IsDefenseless)
            {
                BannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.InAndOut);
            }
            else
            {
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Remove(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Remove(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.InAndOut);
            }
            HasAddedShips = true;
        }
        public void RemoveShip(Ship ship)
        {
            _ships.Remove(ship);
            if (IsSelected && !Stage.IsTraining)
            {
                Stage.Menus.ActionBox.SetSquadsText();
            }
        }       



        // Utility methods
        public override string ToString()
        {
            return $"Squad Number #{SquadNumber} on side #{Side} {Name} with {_ships.Count} ships";
        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Squad x = obj as Squad;
            if (x == null)
            {
                return false;
            }

            return ItemId == x.ItemId;
        }

        public bool Equals(Squad other)
        {
            return ItemId == other.ItemId;
        }

        public override int GetHashCode()
        {
            return ItemId.GetHashCode();
        }

        public static bool operator ==(Squad a, Squad b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.ItemId == b.ItemId;
        }

        public static bool operator !=(Squad a, Squad b)
        {
            return !(a == b);
        }


        /* Range and distance methods */
        public bool CanSeeSquad(Squad squad)
        {
            bool canSee = false;
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                List<Ship> squadShips = squad.GetShips();
                foreach (Ship squadShip in squadShips)
                {
                    if (ship.CanSeeShip(squadShip))
                    {
                        return true;
                    }
                }
            }
            return canSee;
        }
        public bool IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(Squad squad)
        {
            return GetShips().Any((ship) => ship.IsAnySquadShipWithinRange(squad));
        }
        public bool IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().Any((ship) => ship.AreAllSquadShipsWithinRange(squad));
        }
        public bool AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().All((s) => s.AreAllSquadShipsWithinRange(squad));
        }
        /// <summary>
        /// Checks if every ship is this squad has at least one ship of the other squad within range
        /// </summary>
        /// <param name="squad"></param>
        /// <returns></returns>
        public bool AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().All((s) => s.IsAnySquadShipWithinRange(squad));
        }
        public bool IsWithinRangeOfAnyShipInEnemySquad()
        {
            Squad enemy = GetEnemy();
            if (enemy != null)
            {
                return enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this);
            }
            return false;
        }
        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }
        /// <summary>
        /// The calculated center point of the squad
        /// </summary>
        /// <returns></returns>
        public Vector2 GetPosition()
        {
            return GetCenterPoint();
        }
        public Vector2 GetLeftMostPoint()
        {
            Ship ship = GetShips().OrderBy((ship) => ship.GetLeftMostPoint().x).ToList().First();
            return new Vector2(ship.GetLeftMostPoint().x, ship.GetY());
        }
        public Vector2 GetRightMostPoint()
        {
            Ship ship = GetShips().OrderByDescending((ship) => ship.GetRightMostPoint().x).ToList().First();
            return new Vector2(ship.GetRightMostPoint().x, ship.GetY());
        }
        public Vector2 GetTopMostPoint()
        {
            Ship ship = GetShips().OrderByDescending((ship) => ship.GetTopMostPoint().y).ToList().First();
            return new Vector2(ship.GetX(), ship.GetTopMostPoint().y);
        }
        public Vector2 GetBottomMostPoint()
        {
            Ship ship = GetShips().OrderBy((ship) => ship.GetBottomMostPoint().y).ToList().First();
            return new Vector2(ship.GetX(), ship.GetBottomMostPoint().y);
        }
        public float GetWidth()
        {
            return Math.Abs(GetLeftMostPoint().x - GetRightMostPoint().x);
        }
        public float GetHeight()
        {
            return Math.Abs(GetTopMostPoint().y - GetBottomMostPoint().y);
        }
        public Vector2 GetCenterPoint()
        {

            // calculate width and height of box
            float width = GetWidth();
            float height = GetHeight();

            // calculate center point of box

            float midX = GetRightMostPoint().x - (width / 2);
            float midY = GetBottomMostPoint().y + (height / 2);

            return new Vector2(midX, midY);
        }
        public float AngleToPoint(Vector2 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        /// <summary>
        /// Finds the point on a circle between the squad's current position, the angle, and the radius (distance) given
        /// </summary>
        /// <param name="angle"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public Vector2 CirclePoint(float angle, float distance)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            Vector2 position = GetPosition();
            return new Vector2((position.x + (Mathf.Cos(angle) * distance)), (position.y + (Mathf.Sin(angle) * distance)));
        }



        // UI Methods
        public void MoveSquadBox()
        {
            //Debug.Log($"Squad #{squadNumber} is moving and the squad box will have width {GetWidth()}, height {GetHeight()}, and center point {GetCenterPoint()}");
            //Debug.Log($"Right most point {GetRightMostPoint()}, Left most point {GetLeftMostPoint()}, Top most point {GetTopMostPoint()}, Bottom most point {GetBottomMostPoint()}");
            if (IsSelected && !Stage.IsTraining)
            {
                SquadBox.SetActive(true);
                SquadBox.transform.localPosition = GetCenterPoint();
                SquadBox.transform.localScale = new Vector3(GetWidth() + 1, GetHeight() + 1, 0);
                if (HasCustomColor)
                {
                    Utilities.SetUIColor(SquadBox, SquadBoxColor);

                }
                if (GetShips().Count == 1)
                {
                    Ship onlyShip = GetShips().First();
                    SquadBox.transform.eulerAngles = onlyShip.transform.eulerAngles;
                }
                else
                {
                    SquadBox.transform.eulerAngles = Vector3.zero;
                }
                HasMovedBox = true;
            }
        }
        public void DeactivateSquadBox()
        {
            if (HasSquadBox)
            {
                SquadBox.SetActive(false);
            }
        }
        public void ShowSquadRanges()
        {
            GetShips().ForEach((ship) => ship.ShowWeaponRanges());
            IsShowingRanges = true;
        }
        public void HideSquadRanges()
        {
            GetShips().ForEach((ship) => ship.HideWeaponRanges());
            IsShowingRanges = false;
        }

    }

}

