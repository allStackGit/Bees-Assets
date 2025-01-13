using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class Squad : MonoBehaviour
    {
        public LevelStage Level;
        public int Side, SquadNumber, OpponentId, Id;
        public long Age;
        public List<ShipDamageStatus> DamageSentToEnemyShipsBySquad = new List<ShipDamageStatus>();
        public Command Command;
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public MatchupStrategy MatchupStrategy; // the matchup strategy belongs to the squad and not the command because it is used to determine the command by making the matchup
        public HashSet<string> BannedStrats = new HashSet<string>();
        public string Status;
        public string Name;
        public Color Color;
        public Color SquadBoxColor;
        public SavedSquad SavedSquad;
        public GameObject SquadBox;
        public SquadTab SquadTab;
        public bool HasMovedBox, IsMatchingSpeed, IsImmobile, CeaseFire, HasAddedShips, IsShowingRanges, IsGrowingSquad, HasCustomColor, HasSquadTab;
        /// <summary>
        /// A squad can be dead for one frame before it is destroyed. It's important to check for the death of a squad on anything run by a timer outside of the squad object
        /// </summary>
        public bool IsDead;
        /// <summary>
        /// Is this squad is selected by the user?
        /// </summary>
        public bool IsSelected;
        public float CurrentSpeed;

        private List<Ship> _ships = new List<Ship>();
        private bool _shouldChase = false;
        private string _chosenShootingStrategy; // there is a shooting strategy attached to the squad because users attach shooting strategies to the squad whereas the AI attaches them to the command

        public long LastKilled => GetShips().Max(s => s.LastKilled);
        public int DamageDone => GetShips().Sum(s => s.FleetShip.DamageDone);
        public int Health => GetShips().Sum(s => s.Health);
        public float Firepower => GetShips().Sum(s => s.Firepower);
        public int MaxRange => GetShips().Max(s => s.MaxRange);
        public int MaxSight => GetShips().Max(s => s.Sight);
        public float TotalSpeed => GetShips().Sum(s => s.Speed);
        public float MaxSpeed => GetShips().Max(s => s.Speed);
        public int Tsv => GetShips().Sum(s => s.Tsv);
        public float SlowestSpeed => GetShips().Min(s => s.Speed);
        public bool IsMoving => GetShips().Any(ship => ship.IsMoving);
        public bool HasCommand => Command != null;
        public bool HasEnemy => HasCommand && Command.HasEnemy;
        public bool IsAttacking => HasCommand && Command.IsAttacking;
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide ? Level.Map.UserStartingPosition : Level.Map.AIStartingPosition;
        public bool IsDefenseless => GetShips().All((s) => s.Firepower == 0);
        public bool HasMiningShips => GetShips().Any((s) => s.IsMiningShip);
        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();
        public bool IsCarrierSquad => this is CarrierSquad;
        public bool IsShootingSquad => GetShips().Any((s) => s.Turrets.Any());
        public bool HasOnlyYellowJackets => GetShips().All((s) => s.ShipType == "Yellow Jacket");
        public bool HasOnlyStrikers => GetShips().All((s) => s.ShipType == "Striker");
        public bool HasOnlyBombers => GetShips().All((s) => s.ShipType == "Striker" || s.ShipType == "Yellow Jacket" || s.ShipType == "Fire Ship");
        public bool HasOnlyBarges => GetShips().All((s) => s.ShipType == "Barge");
        /// <summary>
        /// If this squad belongs to the user side and there is a player
        /// </summary>
        public bool IsUserControlled => Side == ConfigData.Configuration.UserSide && Level.HasPlayer;
        public bool IsHiveMindControlled => Side == ConfigData.Configuration.AISide || (Side == ConfigData.Configuration.UserSide && !Level.HasPlayer);

        /// <summary>
        /// Whether or not the squad's ships have target coordinates. If they do, it hasn't reached the destination
        /// </summary>
        public bool HasReachedDestination => GetShips().All((s) => s.HasReachedDestination);
        public bool HasDestination => GetShips().Any((s) => s.HasTargetCoordinates);
        public bool HasBrain => GetShips().All((s) => s.HasBrain);
        /// <summary>
        /// A squad is in combat if any of its ships are in combat. This is used for Matchup strategies that target squads that are in combat.
        /// </summary>
        public bool InCombat => GetShips().Any((s) => s.InCombat);


        public List<Ship> __Ships;

        // Setup methods
        public void Setup(LevelStage level, SavedSquad savedSquad, string shootingStrategy, bool ceaseFire, bool isMatchingSpeed, bool shouldChase,
            int id, int side, int squadNumber, string name, Color color)
        {
            Level = level;
            SavedSquad = savedSquad;
            Id = id;
            Side = side;
            Status = "idle";
            Name = name;
            Color = color;
            SquadNumber = squadNumber;
            IsMatchingSpeed = isMatchingSpeed;
            CeaseFire = ceaseFire;
            _shouldChase = shouldChase;
            SetShootingStrategy(shootingStrategy);
            SetOpponent();

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
            if (Level.FullCeaseFire || Side == ConfigData.Configuration.AISide && Level.MakeEnemyCeaseFire)
            {
                CeaseFire = true;
            }
        }
        private void SetOpponent()
        {
            if (Side == ConfigData.Configuration.AISide)
            { // ai side
                if (Level.HasPlayer)
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

                if (Level.HasPlayer)
                {
                    SquadBox = Instantiate(Level.SquadBox, new Vector3(0, 0, 0), Quaternion.identity);
                    SquadBox.transform.parent = Level.Map.transform;
                    SquadBox.SetActive(false);
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

                if (ship.ShipType == "Queen")
                {
                    adjustment *= new Vector2(2.75f, 2); // Need larger spacing between the Queen(s) because it's so large
                }
                else if (ship.ShipType == "Bumblebee")
                {
                    adjustment *= 1.2f;
                }
                else if (ship.ShipType == "Barge" || ship.ShipType == "Fire Ship" || ship.ShipType == "Warp Gate")
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
                SquadTab = Level.SquadTabs[SquadNumber - 1];
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

            if (!Level.IsPaused)
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
            if (IsUserControlled && !Level.IsPaused)
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
            if (Level.DoesUserHaveController && IsSelected)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
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
            return Command != null && Command.Type == "Aggressive" && _shouldChase;
        }
        public void StopChasing()
        {
            if (Command != null && Command.Type == "Aggressive")
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
            if (_shouldChase && Command?.Strategy.Name != "Aggressive")
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
                GameState state = Level.GetState();


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

                    if (state.IsSideKilled(Side))
                    {

                        state.GameOver = true;
                    }
                    else
                    {
                        if (state.GetSelectedSquads().Count == 0)
                        {
                            state.SelectSquad(state.GetSquadsBySide(Side).First());
                        }
                    }
                }

                if (IsUserControlled)
                {
                    if (HasSquadTab)
                    {
                        SquadTab.DisableTab();
                    }
                    Destroy(SquadBox);
                    state.DeselectSquad(this);
                }
                Destroy(this);
            }
            

        }
        public Squad GetClosestEnemySquad()
        {
            // Debug.Log($"Number of enemy squads {squads.Count}, {_level.GetState().GetSquads()}");
            return Level.GetState().GetEnemySquads(Side).OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public Squad GetClosestValidFriendlySquad()
        {
            List<Squad> squads = Level.GetState().GetSquadsBySide(Side).Where(squad => !squad.Equals(this) && (!squad.HasCommand || squad.Command.Type != "Closest Friendly")).ToList();
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
            return Level.GetState().GetShipsVisibleToHiveMind(Side).ToList();
        }
        public List<Ship> GetFriendlyShips()
        {
            return Level.GetState().GetShips(Side);
        }
        public List<Ship> GetPotentialEnemies(Squad target)
        {
            
            List<Ship> potentialEnemies = GetEnemyShips();
            List<Ship> enemies = potentialEnemies.Where((s) => s.Squad.Equals(target)).ToList();

            foreach (Ship potentialEnemy in  potentialEnemies)
            {
                if (!potentialEnemy.Squad.Equals(target))
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
                if (!Equals(potentialAlly.Squad))
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
            Level.GetState().AddToSquadsAwaitingHiveMindCommands(this);
        }
        public void MakeMatchupStrat()
        {
            // Can't get any invisible ship types and start by blocking the visible ship types too
            HashSet<string> banned = ConfigData.Configuration.AllShipTypes.ToHashSet();

            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.BeeSide)
            {
                // if you're the bees you can only get available human ship types
                HashSet<string> enemyShips = Level.GetState().GetHumanShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                // if you're the humans you can only get available bee ship types
                HashSet<string> enemyShips = Level.GetState().GetBeeShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            
            string[] bannedTypes = banned.Select((ship) => $"Type {(Utilities.ConvertShipNameToType(ship))}").ToArray();

            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(new GetMatchupStrategy(AddToMatchup(GetShips()), OpponentId, bannedTypes),
                this, Level, ConfigData.StandardMaxTimeOnQueue));
        }
        public static string AddToMatchup(List<Ship> ships)
        {
            //string unsorted = "";
            //StringBuilder stringBuilder = new StringBuilder();
            char[] letters = ships.Select(s => s.ShipTypeLetter.First()).ToArray();
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
            if (Level.OverrideStrats.Count > 0) // [debug]
            {
                BannedStrats.UnionWith(ConfigData.CommandTypes);
                BannedStrats = BannedStrats.Except(Level.OverrideStrats).ToHashSet();

                if (Level.OverrideStrats.Contains("Scouting") && Level.GetState().GetShipsVisibleToHiveMind(Side).Count > 0 && Level.OverrideStrats.Count > 1 && !IsDefenseless)
                {
                    BannedStrats.Add("Scouting");
                }
            }
            HashSet<string> banned = BannedStrats.ToHashSet(); // the ToHashSet is important to prevent modification of the original set

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
                banned.Add("Aggressive");
                banned.Add("Defensive");
                banned.Add("Circle");
                banned.Add("Right Swipe");
                banned.Add("Left Swipe");
                banned.Add("In and Out");
            }

            GameState state = Level.GetState();
            int closestFriendlySquadCount = state.GetSquadsBySide(Side).Where((squad) => squad?.Command?.Strategy.Name == "Closest Friendly").Count();
            int friendlySquadCount = state.GetSquadsBySide(Side).Count;
            if (friendlySquadCount - 1  <= friendlySquadCount)
            {
                banned.Add("Closest Friendly");
            }
            if (!BannedStrats.Contains("Mining") && (!HasMiningShips || !Level.ActivateMining))
            {
                BannedStrats.Add("Mining");
                banned.Add("Mining");
            }
            if (!BannedStrats.Contains("Full Retreat") && (Side != ConfigData.Configuration.HumanSide || !state.HasWarpGates))
            {
                BannedStrats.Add("Full Retreat");
                banned.Add("Full Retreat");
            }

            //if (HasOnlyYellowJackets)
            //{
            //    Debug.Log($"Trying to get a command for {Name} against {enemy?.Name}");
            //    for (int i = 0; i < banned.Count; i++)
            //    {
            //        Debug.Log($"banned #{i} is {banned.ElementAt(i)}");
            //    }
            //}


            ConfigData.Socket.SendRequest(new CommandRequest(new GetStrategy(matchup, OpponentId, banned.ToArray()),
                this, enemy, Level, matchup, ConfigData.StandardMaxTimeOnQueue));


        }
        public void ClearTargets()
        {
            GetShips().ForEach(ship =>
            {
                ship.ClearTargets();
            });
        }
        public void SetShootingStrategy(string strategy)
        {
            if (ConfigData.Configuration.ShootingStrategies.Contains(strategy))
            {
                _chosenShootingStrategy = strategy;
            }
        }
        public string GetShootingStrategy()
        {
            if (HasCommand && Command.HasShootingStrategy)
            {
                return Command.ShootingStrategy.Name;
            }
            return _chosenShootingStrategy;
        }
        public string GetCommandStrategy()
        {
            if (HasCommand && Command.HasStrategy)
            {
                return Command.Strategy.Name;
            }
            return null;
        }
        public void UserGuard(Squad squad)
        {

            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Guard", null);
            if (strategies.Item1 != null)
            {
                ((Guard)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, squad);
            }
            
            if (Level.DoesUserHaveController)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            //Debug.Log($"Selecting patrol area for {Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Patrol", null);
            if (strategies.Item1 != null)
            {
                ((Patrol)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, topLeft, bottomRight);
            }
            
            if (Level.DoesUserHaveController)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserMining(MiningAsteroid miningAsteroid)
        {
            if (HasMiningShips)
            {
                (Strategy, ShootingStrategy) strategies = MakeUserCommand("Mining", null);
                if (strategies.Item1 != null)
                {
                    ((Mining)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, miningAsteroid);
                }
            }
            else
            {
                if (FinalizeUserCommand())
                {
                    Move(miningAsteroid.GetPosition());
                }
            }

        }
        public void UserFullRetreat(WarpGate warpGate)
        {
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Full Retreat", null);
            if (strategies.Item1 != null)
            {
                ((FullRetreat)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, warpGate);
            }
            

        }
        public void UserAggressive(Squad enemy)
        {
            if (HasOnlyBombers)
            {
                UserBombingRun(enemy);
                return;
            }
            //else if (HasOnlyBarges)
            //{
            //    UserCharge(enemy);
            //    return;
            //}
            //Debug.Log($"Creating \"Aggressive\" command for {Name} against {enemy.Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Aggressive", enemy);
            if (strategies.Item1 != null)
            {
                Command.Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), false);
            }
            
        }
        public void UserBombingRun(Squad enemy)
        {
            //Debug.Log($"Creating \"Bombing Run\" command for {Name} against {enemy.Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Bombing Run", enemy);
            if (strategies.Item1 != null)
            {
                ((BombingRun)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), false);
            }
            
        }
        //public void UserCharge(Squad enemy)
        //{
        //    //Debug.Log($"Creating \"Bombing Run\" command for {Name} against {enemy.Name}");
        //    (Strategy, ShootingStrategy) strategies = MakeUserCommand("Charge", enemy);
        //    if (strategies.Item1 != null)
        //    {
        //        ((Charge)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), false);
        //    }
        //}
        public (Strategy, ShootingStrategy) MakeUserCommand(string command, Squad enemy)
        {
            //Debug.Log($"{Name} now has command against {enemy.Name}");

            if (FinalizeUserCommand())
            {
                MatchupStrategy = null;

                switch (command)
                {
                    case "Aggressive":
                        Command = gameObject.AddComponent<Aggressive>();
                        break;
                    case "Bombing Run":
                        Command = gameObject.AddComponent<BombingRun>();
                        break;
                    //case "Charge":
                    //    Command = gameObject.AddComponent<Charge>();
                    //    break;
                    case "Guard":
                        Command = gameObject.AddComponent<Guard>();
                        break;
                    case "Patrol":
                        Command = gameObject.AddComponent<Patrol>();
                        break;
                    case "Mining":
                        Command = gameObject.AddComponent<Mining>();
                        break;
                    case "Full Retreat":
                        Command = gameObject.AddComponent<FullRetreat>();
                        break;
                    default:
                        Debugger.Exception($"Invalid command {command} issued to user squad");
                        break;
                }



                Command.Setup(this, false, enemy, null);

                return (new Strategy(Command, command, null, 0, 0), new ShootingStrategy(Command, GetShootingStrategy(), null, 0, 0));
            }
            else
            {
                return (null, null);
            }

            


        }
        public bool FinalizeUserCommand()
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
                if (Command.Type == "Guard")
                {
                    UnmatchSpeed();
                    ((Guard)Command).GetGuardingSquads().ForEach((squad) =>
                    {
                        ((Guard)squad.Command).OtherGuardSquads.Remove(this);
                    });
                }
                Command.SetFinalize("New command given");
                return true;
                return false;
                
            }
            return true;
        }
        public MiningAsteroid GetNearestMiningAsteroid()
        {
            return (MiningAsteroid)Level.GetState().GetObstacles().Where((o) => o.IsMiningAsteroid).OrderBy((o) => DistanceToPoint(o.GetPosition())).FirstOrDefault();
        }


        // ship list methods
        public List<Ship> GetShips()
        {
            return _ships;
        }
        /// <summary>
        /// Finds the damage status entry for this ship in the squads list or creates it if it doesn't exist
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public ShipDamageStatus GetShipDamageStatus(Ship potentialTargetShip)
        {
            List<ShipDamageStatus> damageSentToEnemyShipsBySquad = DamageSentToEnemyShipsBySquad;
            ShipDamageStatus shipDamageStatus = null;
            if (damageSentToEnemyShipsBySquad.Count > 0)
            {
                shipDamageStatus = damageSentToEnemyShipsBySquad.FirstOrDefault(s => s != null && s.Ship != null && s.Ship.Equals(potentialTargetShip));
            }

            if (shipDamageStatus == null)
            {
                shipDamageStatus = new ShipDamageStatus(potentialTargetShip);
                damageSentToEnemyShipsBySquad.Add(shipDamageStatus);
            }
            return shipDamageStatus;
        }
        public void AddShip(Ship ship)
        {
            //Debug.Log($"Adding {ship.Name} to Squad {Name}");
            _ships.Add(ship);
            if (IsDefenseless)
            {
                BannedStrats.Add("Aggressive");
                BannedStrats.Add("Circle");
                BannedStrats.Add("Right Swipe");
                BannedStrats.Add("Left Swipe");
                BannedStrats.Add("In and Out");
            }
            else
            {
                BannedStrats.Remove("Aggressive");
                BannedStrats.Remove("Circle");
                BannedStrats.Remove("Right Swipe");
                BannedStrats.Remove("Left Swipe");
                BannedStrats.Remove("In and Out");
            }
            HasAddedShips = true;
        }
        public void RemoveShip(Ship ship)
        {
            _ships.Remove(ship);
            if (IsSelected && Level.HasPlayer)
            {
                Level.Menus.ActionBox.SetSquadsText();
            }
        }       



        // Utility methods
        public bool Equals(Squad squad)
        {
            return squad.Id == Id;
        }
        public override string ToString()
        {
            return $"Squad Number #{SquadNumber} on side #{Side} {Name} with {_ships.Count} ships";
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
            if (IsSelected && Level.HasPlayer)
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
            if (SquadBox != null)
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

