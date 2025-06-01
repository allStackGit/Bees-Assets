using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Squad : MonoBehaviour
    {
        public Level Level;
        public Stage Stage;
        public int Side, SquadNumber, OpponentId;
        public long Id;
        /// <summary>
        /// The Id of this squad relative to the stage. Guarenteed unique for this stage. Not the same as the saved squad Id
        /// </summary>
        public int ItemId;
        public long Age;
        public ConfigData.SquadTypes SquadType;
        private Command _command;
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        /// <summary>
        /// The matchup strategy belongs to the squad and not the command because it is used to determine the command by making the matchup. 
        /// It should also be attached to the command so that it can be stored to the server
        /// </summary>
        public MatchupStrategy MatchupStrategy = new MatchupStrategy(); 
        public HashSet<ConfigData.CommandTypes> BannedStrats = new HashSet<ConfigData.CommandTypes>();
        public string Status;
        public string Name;
        public Color Color;
        public Color SquadBoxColor;
        public SavedSquad SavedSquad;
        /// <summary>
        /// The Colored, semi-transparent box that shows up behind a squad when it's selected
        /// </summary>
        public GameObject SquadBox;
        public SquadTab SquadTab;
        public bool HasMovedBox, IsMatchingSpeed, IsImmobile, CeaseFire, HasAddedShips, IsShowingRanges, IsGrowingSquad, HasCustomColor, HasSquadTab, HasSquadBox;
        public Vector2 Destination;
        /// <summary>
        /// It's important to check for the death of a squad on anything run by a timer outside of the squad object
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
        /// <summary>
        /// A squad has a command as soon as the command is setup and before it's ever executed
        /// </summary>
        public bool HasCommand;
        public float CurrentSpeed;
        public int CreationId;
        public int OriginalCommandId;

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
        public bool HasEnemy => HasCommand && GetCommand().HasEnemy;
        public bool IsAttacking => HasCommand && GetCommand().IsAttacking;
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide ? Level.Map.UserStartingPosition : Level.Map.AIStartingPosition;
        public bool IsDefenseless => GetShips().All((s) => s.Firepower == 0);
        public bool HasMiningShips => GetShips().Any((s) => s.IsMiningShip);
        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();
        public bool HasOnlyStrikers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker);
        public bool HasOnlyBombers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker || s.ShipType == ConfigData.ShipTypes.YellowJacket || 
        s.ShipType == ConfigData.ShipTypes.FireBarge);
        public bool HasOnlyBarges => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Barge);
        public bool HasOnlyWarpGates => GetShips().All((s) => s.IsWarpGate);
        public bool HasOnlyBeehives => GetShips().All((s) => s.IsBeehive);

        /// <summary>
        /// Whether or not the squad's ships have target coordinates. If they do, it hasn't reached the destination
        /// </summary>
        public bool HasReachedDestination => GetShips().All((s) => s.HasReachedDestination);
        public bool HasDestination => GetShips().Any((s) => s.HasTargetCoordinates);
        /// <summary>
        /// A squad is in combat if any of its ships are in combat. This is used for Matchup strategies that target squads that are in combat.
        /// </summary>
        public bool InCombat => GetShips().Any((s) => s.InCombat);

        /// <summary>
        /// This is used to temporarily hold a list of ships for a method. In theory, it should be reset every time it's used, and since everything happens in sequence,
        /// nothing should be accessing this simultaneously
        /// </summary>
        private List<Ship> _tempShips;
        private Ship _tempShip;
        private Squad _tempSquad;
        private List<Squad> _tempSquads;
        private Vector2 _tempPosition;

        // Setup methods
        public virtual void ClearData()
        {
            SetCommandNull();
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
            //MatchupStrategy.Kill();
            enabled = true;

        }
        public virtual void Create(Stage stage)
        {
            SquadType = ConfigData.SquadTypes.Squad;
            Stage = stage;
            IsDead = true;
            enabled = false;
            //CreationId = Utilities.Hash();
            //Debug.Log($"Created squad {this}");
        }
        private ScaledTimer _checkChaseTimer = new ScaledTimer();
        public void Setup(Level level, SavedSquad savedSquad, ConfigData.ShootingStrategyTypes shootingStrategy, bool ceaseFire, bool isMatchingSpeed, bool shouldChase,
            bool isImobile, long id, int side, int squadNumber, string name, Color color)
        {
            ClearData();
            IsImmobile = isImobile;
            Level = level;
            SavedSquad = savedSquad;
            Id = id;
            Side = side;
            Name = name;
            Color = color;
            ItemId = Level.State.GetId();
            SquadNumber = squadNumber;
            IsMatchingSpeed = isMatchingSpeed;
            CeaseFire = ceaseFire;
            _shouldChase = shouldChase;
            SetShootingStrategy(shootingStrategy);
            SetOpponent();
            SetSquadBox();

            IsUserControlled = Side == ConfigData.Configuration.UserSide && Level.HasPlayer; 
            if (!IsUserControlled)
            {
                IsHiveMindControlled = true;
            }
            else
            {
                IsHiveMindControlled = false;
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

            transform.parent = Level.Map.Transform;

            if (IsUserControlled)
            {
                _checkChaseTimer.Reuse(1, CheckChase, true);
                Level.AddTimer(_checkChaseTimer);
                //InvokeRepeating(nameof(CheckChase), 5, 1);
            }
            if (Stage.FullCeaseFire || Side == ConfigData.Configuration.AISide && Stage.MakeEnemyCeaseFire)
            {
                SetSquadCeaseFire(true);
               
            }

            //Debug.Log($"Setup squad {this}");

        }
        public void SetSquadCeaseFire(bool ceasefire)
        {
            CeaseFire = ceasefire;
            GetShips().ForEach((s) => {
                s.IsCeaseFire = CeaseFire;
            });
        }
        private void SetSquadBox()
        {
            if (!Stage.IsTraining)
            {
                if (!HasSquadBox)
                {
                    SquadBox = Instantiate(Stage.Prefabs.SquadBoxPrefab, Vector2.zero, Quaternion.identity);
                    HasSquadBox = true;
                }
                SquadBox.transform.parent = Level.Map.Transform;
                SquadBox.SetActive(false);
                SquadBox.name = $"{Name} - Squadbox"; // [note] [testing] Only used for testing
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
                
            }
        }

        private Vector2 _adjustment;
        private Vector2 _queenMultiplier = new Vector2(2.75f, 2);
        private Vector2 _wideMultiplier = new Vector2(1.4f, 1);
        /// <summary>
        /// When squads are setup, they are position centrally like a line formation, first in the center, second on the right, third on the left, fourth on the right, and so on
        /// The squad is set to the center point and all the ships are positioned based off their original offset
        /// </summary>
        /// <param name="position"></param>
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

                _adjustment = ship.OffsetFromCenter;

                if (ship.ShipType == ConfigData.ShipTypes.Queen)
                {
                    _adjustment *= _queenMultiplier; // Need larger spacing between the Queen(s) because it's so large
                }
                else if (ship.ShipType == ConfigData.ShipTypes.Bumblebee)
                {
                    _adjustment *= 1.2f;
                }
                else if (ship.ShipType == ConfigData.ShipTypes.Barge || ship.ShipType == ConfigData.ShipTypes.FireBarge || ship.ShipType == ConfigData.ShipTypes.WarpGate)
                {
                    _adjustment *= _wideMultiplier;
                }

                //Debug.Log($"Sizefactor for {ship.Name}: {sizeFactor}");
                //ship.transform.localPosition = Level.ForceBounds((position.x + adjustment.x), (position.y + adjustment.y));
                ship.transform.localPosition = new Vector2(position.x + _adjustment.x, position.y + _adjustment.y);
                //Debug.Log($"Local starting position for {ship.Name}: {ship.transform.localPosition}");

            });

        }
        public void SetSquadTab()
        {
            if (IsUserControlled && SquadNumber <= 10)
            {
                //Debug.Log($"Stage: {Stage}, SquadNumber: {SquadNumber}");
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
        //protected void Update() // [testing]
        //{
        //    //if (!IsDead && GetShips().Count == 0)
        //    //{
        //    //    Debug.LogError($"{Name} has no ships and isn't dead at frame #{Stage.__Updates}");
        //    //}
        //    //else
        //    //{
        //    //    Debug.Log($"{Name} has {GetShips().Count} ships and isDead? {IsDead} at frame #{Stage.__Updates}");
        //    //}


        //    //if (!Level.State.IsPaused)
        //    //{
        //    //    Age++;
        //    //    if (HasCommand)
        //    //    {
        //    //        GetCommand().Age++;
        //    //    }
        //    //}

        //    //if (!IsDead && HasCommand && OriginalCommandId > 0 && OriginalCommandId != GetCommand().ItemId || (HasCommand && PastCommands.Any((pc) => pc.OutcomeId != GetCommand().OutcomeId && !pc.IsFinalized)))
        //    //{
        //    //    Debug.LogError($"{this} no longer has the original command id! #{GetCommand().ItemId}");
        //    //}

        //    // Debug.Log($"{name} ship has lived for {tickLife} ticks and {ShowLifeTime()} seconds with {GetHealth()} health");
        //}
        public void FixedUpdate()
        {
            if (IsUserControlled)
            {
                HasMovedBox = false;
            }
            //UpdateTestProperties();
        }
        private Vector2 _center;
        public void SetOffsets()
        {
            _center = GetCenterPoint();
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                ship.OffsetFromCenter = new Vector2(ship.GetX() - _center.x, ship.GetY() - _center.y);
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
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
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
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                //Debug.Log($"Unmatching speed for {ship.Name} and setting speed to {ship.Speed}");
                ship.SetCurrentSpeed(ship.Speed);
            }
            IsMatchingSpeed = false;
        }
        public void SetSquadSpeed(float speed)
        {
            CurrentSpeed = speed;
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                //Debug.Log($"Matching speed for {ship.Name} and setting speed to {speed}");
                ship.SetCurrentSpeed(speed);
            }
        }
        public void SetChase(bool chase)
        {
            _shouldChase = chase;
        }
        public bool IsChasing()
        {
            return HasCommand && GetCommand().CommandType == ConfigData.CommandTypes.Aggressive && _shouldChase;
        }
        public void StopChasing()
        {
            if (HasCommand && GetCommand().CommandType == ConfigData.CommandTypes.Aggressive)
            {
                GetCommand().SetFinalize("Stopped Chasing");
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
            if (_shouldChase && GetCommand()?.CommandType != ConfigData.CommandTypes.Aggressive && !Level.State.GameOver)
            {
                _tempSquad = GetClosestEnemySquad();
                //Debug.Log($"The closest Enemy to {Name} is {closestSquad.Name}");
                if (CanSeeSquad(_tempSquad))
                {
                    //Debug.Log($"Initiating chase by {Name} against {closestSquad.Name}.");
                    UserAggressive(_tempSquad);
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
                    //if (HasCommand)
                    //{
                    //    Command.SquadKilled();
                    //}

                    if (IsUserControlled)
                    {
                        DeactivateSquadBox();
                    }

                    if (Level.State.IsSideKilled(Side))
                    {

                        Level.State.GameOver = true;
                    }
                }

                if (HasCommand)
                {
                    //Debug.Log($"Squad got killed {this}");
                    GetCommand().SquadKilled();
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
                Level.CancelTimer(_checkChaseTimer);
                Level.State.RemoveSquad(this);
                enabled = false;
                //Stage.Pool.ReturnSquadToPool(this);
            }
            

        }
        /// <summary>
        /// Returns the closest enemy squad visible to the hivemind or simply the closest squad if this is the user
        /// </summary>
        /// <returns></returns>
        public Squad GetClosestEnemySquad()
        {
            // Debug.Log($"Number of enemy squads {squads.Count}, {_Level.State.GetSquads()}");
            //Debug.Log($"Getting closest enemy squad for {Name}");
            return Level.State.GetSquadsVisibleToHiveMind(Side).OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public Squad GetClosestValidFriendlySquad()
        {
            _tempSquads = Level.State.GetSquadsBySide(Side).Where(squad => squad != this && (!squad.HasCommand || squad.GetCommand().CommandType != ConfigData.CommandTypes.ClosestFriendly)).ToList();
            return _tempSquads.OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        //public Squad GetEnemy()
        //{
        //    if (HasCommand)
        //    {
        //        return GetCommand().EnemySquad;
        //    }
        //    else
        //    {
        //        if (IsUserControlled)
        //        {
        //            return GetClosestEnemySquad();
        //        }
        //        return null;
        //    }
        //}
        /// <summary>
        /// Gets all ships in the level that are on the opposing side and visible to the hive mind
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
        private List<Ship> _enemies;
        /// <summary>
        /// Returns all visible ships in the enemy squad plus all enemy ships that have those previous ship in their range plus all enemy ships that have our squad within range
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        public List<Ship> GetPotentialEnemies(Squad target)
        {

            _tempShips = GetEnemyShips();
            _enemies = _tempShips.Where((s) => s.Squad == target).Take(64).ToList();

            foreach (Ship potentialEnemy in _tempShips)
            {
                if (!potentialEnemy.Squad == target && _enemies.Count <= 64)
                {
                    if (potentialEnemy.IsAnySquadShipWithinRange(this)) // if the squad is within the range of the potential enemy
                    {
                        _enemies.Add(potentialEnemy);
                        Debug.LogWarning($"We added a potential enemy of type {potentialEnemy.ShipType} against target {target}");
                    }
                }
            }

            return _enemies;
        }
        private List<Ship> _allies;
        private int _limit;
        public List<Ship> GetPotentialAllies(Squad target)
        {
            //_tempShips = GetShipsForMatchup(); // don't need this anymore since we get the ships in this squad beforehand
            _allies = GetFriendlyShips();
            _limit = 64 - GetShips().Count;

            foreach (Ship potentialAlly in _allies)
            {
                if (this != potentialAlly.Squad && _tempShips.Count <= _limit)
                {
                    if (potentialAlly.IsAnySquadShipWithinRange(target)) // if any ship in the target squad is within the range of another of its allies
                    {
                        _tempShips.Add(potentialAlly);
                    }
                }
            }

            return _tempShips;
        }


        // Command and control methods
        public HashSet<ConfigData.CommandTypes> MovementAttackTypes = new HashSet<ConfigData.CommandTypes>{ ConfigData.CommandTypes.CircleSquad, ConfigData.CommandTypes.RightSwipe, ConfigData.CommandTypes.LeftSwipe,
        ConfigData.CommandTypes.InAndOut,  ConfigData.CommandTypes.BombingRun };
        public bool HasMovementAttackType;
        /// <summary>
        /// Clears cached variables that relate to the command
        /// </summary>
        public void ResetCommandCache()
        {
            HasMovementAttackType = MovementAttackTypes.Contains(GetCommand().CommandType);
        }
        public Command GetCommand()
        {
            return _command;
        }
        public void SetCommand(Command command)
        {
            //Debug.Log($"Setting {this} Command to {command}");
            _command = command;
            ResetCommandCache();
        }
        public void SetCommandNull()
        {
            _command = null;
        }
        /// <summary>
        /// Puts this squad on the list of squads waiting for new hive mind commands
        /// </summary>
        public void AddToCommandList()
        {          
            //if (GetShips().Any((s) => s.ShipType == ConfigData.ShipTypes.Beacon))
            //{
            //    Debug.LogError($"{this} has beacons and is being added to the command list");
            //}
            Level.State.AddToSquadsAwaitingHiveMindCommands(this);
        }
        private HashSet<ConfigData.ShipTypes> _banned, _enemyShips;
        private string[] _bannedTypes;
        public List<Ship> GetShipsForMatchup()
        {
            return GetShips().Take(64).ToList(); // the ToList() is very important to prevent this list from modifying the main squad list
        }
        public void MakeMatchupStrat()
        {
            // Can't get any invisible ship types and start by blocking the visible ship types too
            _banned = ConfigData.Configuration.AllShipTypes;

            if (Side == ConfigData.Configuration.BeeSide)
            {
                // if you're the bees you can only get available human ship types that are on this level because all bee ships are banned as matchup strategies as well
                // as all human ship types that are not on this level
                _enemyShips = Level.State.GetHumanShipTypes();
                _banned = _banned.Where((type) => !_enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                // if you're the humans you can only get available bee ship types because all human ships are banned as matchup strategies
                _enemyShips = Level.State.GetBeeShipTypes();
                _banned = _banned.Where((type) => !_enemyShips.Contains(type)).ToHashSet();
            }

            _bannedTypes = _banned.Select((ship) => $"Type {(Utilities.ConvertShipTypeToCharacter[ship])}").ToArray();
            //Debug.Log($"Making matchup strategy for {Name} and the following ships are banned: {Utilities.ListToString(_banned.ToList())}");

            //if (GetShips().Any((s) => s.ShipType == ConfigData.ShipTypes.Beacon))
            //{
            //    Debug.LogError($"{this} has beacons and is trying to make a matchup strategy");
            //}
            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(new GetMatchupStrategy(AddToMatchup(GetShipsForMatchup()), OpponentId, _bannedTypes),
                this, Level, ConfigData.StandardMaxTimeOnQueue));
        }
        private static char[] _letters;
        public static string AddToMatchup(List<Ship> ships)
        {
            //string unsorted = "";
            //StringBuilder stringBuilder = new StringBuilder();
            _letters = ships.Select(s => Utilities.ConvertShipTypeLetterToCharacter[s.ShipTypeLetter]).ToArray();
            //ships.ForEach((ship) =>
            //{
            //    //unsorted += ship.ShipTypeLetter;
            //    //stringBuilder.Append(ship.ShipTypeLetter);
            //    letters.Add(ship.ShipTypeLetter.First());
            //});


            
            Array.Sort(_letters);
            //Debug.Log($"matchup: {new string(_letters)}");
            return new string(_letters);
        }
        private string _matchup;
        private StringBuilder _sb = new StringBuilder();
        private HashSet<ConfigData.CommandTypes> _bannedStrats;
        private int _atTheWalls, _comparativeHealth, _friendlySquadCount, _closestFriendlySquadCount;
        private int _distance = 15;
        private List<Ship> _matchupAllies;
        private List<Ship> _matchupEnemies;

        public void MakeMatchupAndGetCommand(Squad enemy = null)
        {
            if (Level.Stage.OverrideStrats.Count > 0) // [debug]
            {
                BannedStrats.UnionWith(ConfigData.TypesOfCommands);
                BannedStrats = BannedStrats.Except(Level.Stage.OverrideStrats).ToHashSet();

                if (Level.Stage.OverrideStrats.Contains(ConfigData.CommandTypes.Scouting) && Level.State.GetShipsVisibleToHiveMind(Side).Count > 0 && Level.Stage.OverrideStrats.Count > 1 && !IsDefenseless)
                {
                    BannedStrats.Add(ConfigData.CommandTypes.Scouting);
                }
            }
            _bannedStrats = BannedStrats.ToHashSet(); // the ToHashSet is important to prevent modification of the original set

            _sb = _sb.Clear();
            _sb.Append(AddToMatchup(GetShipsForMatchup()));

            if (enemy != null)
            {
                _matchupEnemies = GetPotentialEnemies(enemy);
                if (_matchupEnemies.Count == 0)
                {
                    //Debug.LogWarning($"{this} has a matchup against {enemy} but there are no enemy ships visible to the hive mind so we are putting it back on the command queue: {Utilities.ListToString(Level.State.GetShipsVisibleToHiveMind(Side).ToList())}");
                    AddToCommandList();
                    return;
                }
                _matchupAllies = GetPotentialAllies(enemy);

                /*
                This is the calculation for the enemy's current average percentage of health for each ship and then the same for the allies, and then compares the allies to the enemies
                 */

                _comparativeHealth = (int)Math.Round((Ship.GetAverageHealthPercent(_matchupAllies) / Ship.GetAverageHealthPercent(_matchupEnemies)) * 100);

                /*
                 comparativeHealth
                 < 50 0
                 50 - 85 1
                 85 - 115 2
                 115 - 165 3
                 165+ 4
                 */

                if (_comparativeHealth < 50)
                {
                    _comparativeHealth = 0;
                }
                else if (_comparativeHealth < 85)
                {
                    _comparativeHealth = 1;
                }
                else if (_comparativeHealth < 115)
                {
                    _comparativeHealth = 2;
                }
                else if (_comparativeHealth < 165)
                {
                    _comparativeHealth = 3;
                }
                else
                {
                    _comparativeHealth = 4;
                }
                _sb.Append(AddToMatchup(_matchupAllies));
                _sb.Append("|");
                _sb.Append(AddToMatchup(_matchupEnemies));
                _sb.Append("|");
                _sb.Append((enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this) ? 1 : 0));
                _sb.Append("|");
                _sb.Append(_comparativeHealth);


            }
            else
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                _bannedStrats.Add(ConfigData.CommandTypes.Retreat);
                _bannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                _bannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                _bannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                _bannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                _bannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
                _bannedStrats.Add(ConfigData.CommandTypes.Hold);

                _sb.Append("||0|0"); // fills in the matchup string for seeing no enemies and having no allies

            }

            // Determines whether or not the squad is at the "walls"
            _atTheWalls = 0;
            _tempPosition = GetPosition();
            if (_tempPosition.x < (Level.MinX + _distance) || _tempPosition.x > (Level.MaxX - _distance)) // check if it's at the sides
            {
                _atTheWalls = 1;
                if (_tempPosition.y < (Level.MinY + _distance) || _tempPosition.y > (Level.MaxY - _distance))
                {
                    _atTheWalls = 2;
                }
            }
            else if (_tempPosition.y < (Level.MinY + _distance) || _tempPosition.y > (Level.MaxY - _distance))
            {
                _atTheWalls = 1;
            }

            _sb.Append("|");
            _sb.Append(_atTheWalls);
            _matchup = _sb.ToString();


            _closestFriendlySquadCount = Level.State.GetSquadsBySide(Side).Where((squad) => squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.ClosestFriendly).Count();
            _friendlySquadCount = Level.State.GetSquadsBySide(Side).Count;
            if (_friendlySquadCount - 1  <= _closestFriendlySquadCount)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.ClosestFriendly);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.Mining) && (!Level.ActivateMining || !HasMiningShips))
            {
                BannedStrats.Add(ConfigData.CommandTypes.Mining);
                _bannedStrats.Add(ConfigData.CommandTypes.Mining);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.FullRetreat) && (Side != ConfigData.Configuration.HumanSide || !Level.State.HasWarpGates || HasOnlyWarpGates))
            {
                BannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
                _bannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.Heal) && (Side != ConfigData.Configuration.BeeSide || !Level.State.HasBeehives || HasOnlyBeehives))
            {
                BannedStrats.Add(ConfigData.CommandTypes.Heal);
                _bannedStrats.Add(ConfigData.CommandTypes.Heal);
            }
            else if (Level.State.GetBeeShips().Where((s) => s.IsBeehive && ((Beehive)s).ShipsHealingHere.Count < 4).Count() == 0) // If there are beehives but they are all full, temporarily ban the heal strategy
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Heal);
            }

            //if (HasOnlyYellowJackets)
            //{
            //    Debug.Log($"Trying to get a command for {Name} against {enemy?.Name}");
            //    for (int i = 0; i < banned.Count; i++)
            //    {
            //        Debug.Log($"banned #{i} is {banned.ElementAt(i)}");
            //    }
            //}

            //Debug.Log($"{this} has {_bannedStrats.Count} banned strats again enemy: {enemy} with matchup: {_matchup} and {BannedStrats.Count} permabanned strats");
            ConfigData.Socket.SendRequest(new CommandRequest(new GetStrategy(_matchup, OpponentId, _bannedStrats.Select(b => Utilities.ConvertCommandTypeToName[b]).ToArray()),
                this, enemy, Level, _matchup, ConfigData.StandardMaxTimeOnQueue));


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
            if (HasCommand && GetCommand().HasShootingStrategy)
            {
                GetCommand().ShootingStrategy.ShootingStrategyType = strategy;
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
            if (HasCommand)
            {
                return GetCommand().CommandType;
            }
            return ConfigData.CommandTypes.Uninitialized;
        }
        public void UserGuard(Squad squad)
        {

            MakeUserCommand(ConfigData.CommandTypes.Guard, null);
            ((Guard)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, squad);
            
            if (Level.Stage.DoesUserHaveController)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            MakeUserCommand(ConfigData.CommandTypes.Patrol, null);
            ((Patrol)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, topLeft, bottomRight);
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
                ((Mining)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, miningAsteroid);
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
            ((FullRetreat)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, warpGate);

        }
        public void UserHeal(List<Beehive> beehives)
        {
            //Debug.Log($"Starting new heal command for {Name}");
            MakeUserCommand(ConfigData.CommandTypes.Heal, null);
            ((Heal)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, beehives);

        }
        public void UserAggressive(Squad enemy)
        {
            if (HasOnlyBombers)
            {
                UserBombingRun(enemy);
                return;
            }
            MakeUserCommand(ConfigData.CommandTypes.Aggressive, enemy);
            ((Aggressive)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);

        }
        public void UserBombingRun(Squad enemy)
        {
            //Debug.Log($"Creating \"Bombing Run\" command for {Name} against {enemy.Name}");
            MakeUserCommand(ConfigData.CommandTypes.BombingRun, enemy);
            ((BombingRun)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);

        }
        public void MakeUserCommand(ConfigData.CommandTypes command, Squad enemy)
        {
            //Debug.Log($"{Name} now has command against {enemy.Name}");
            FinalizeUserCommand();

            switch (command)
            {
                case ConfigData.CommandTypes.Aggressive:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive));
                    break;
                case ConfigData.CommandTypes.BombingRun:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun));
                    break;
                //case ConfigData.CommandTypes.Charge:
                //    Command = gameObject.AddComponent<Charge>();
                //    break;
                case ConfigData.CommandTypes.Guard:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Guard));
                    break;
                case ConfigData.CommandTypes.Patrol:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Patrol));
                    break;
                case ConfigData.CommandTypes.Mining:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Mining));
                    break;
                case ConfigData.CommandTypes.FullRetreat:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.FullRetreat));
                    break;
                case ConfigData.CommandTypes.Heal:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Heal));
                    break;
                default:
                    Debug.LogError($"Invalid command {command} issued to user squad");
                    break;
            }


            GetCommand().Setup(this, false, enemy, null);

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
                if (GetCommand().CommandType == ConfigData.CommandTypes.Guard)
                {
                    UnmatchSpeed();
                    ((Guard)GetCommand()).GetGuardingSquads().ForEach((squad) =>
                    {
                        ((Guard)squad.GetCommand()).OtherGuardSquads.Remove(this);
                    });
                }
                GetCommand().SetFinalize("New command given");
                
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
                BannedStrats.Add(ConfigData.CommandTypes.Hold);
            }
            else if (HasOnlyBombers)
            {
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);


                BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Add(ConfigData.CommandTypes.Hold);
            }
            else
            {
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Remove(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Remove(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Remove(ConfigData.CommandTypes.Hold);
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
            return $"Squad {Name} with {_ships.Count} ships (#{ItemId}, #{CreationId})";
        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _tempSquad = obj as Squad;
            if (_tempSquad == null)
            {
                return false;
            }

            return ItemId == _tempSquad.ItemId;
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
        private List<Ship> _squadShips;
        public bool CanSeeSquad(Squad squad)
        {
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                _squadShips = squad.GetShips();
                foreach (Ship squadShip in _squadShips)
                {
                    if (ship.CanSeeShip(squadShip))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        /// <summary>
        /// Loops through every ship in our squad and checks if any ship in the enemy squad is within range
        /// </summary>
        /// <param name="squad"></param>
        /// <returns></returns>
        public bool IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(Squad enemy)
        {
            return GetShips().Any((ship) => ship.IsAnySquadShipWithinRange(enemy));
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

        //public bool IsWithinRangeOfAnyShipInEnemySquad()
        //{
        //    _tempSquad = GetEnemy();
        //    if (_tempSquad != null)
        //    {
        //        return _tempSquad.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this);
        //    }
        //    return false;
        //}
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
            _tempShip = GetShips().OrderBy((ship) => ship.GetLeftMostPoint().x).ToList().First();
            return new Vector2(_tempShip.GetLeftMostPoint().x, _tempShip.GetY());
            //try
            //{
            //    Ship ship = GetShips().OrderBy((ship) => ship.GetLeftMostPoint().x).ToList().First();
            //    return new Vector2(ship.GetLeftMostPoint().x, ship.GetY());
            //}catch (Exception e)
            //{
            //    Debug.Log($"Squad: {Name}, ShipCount: {GetShips().Count} Ships: {Utilities.ListToString(GetShips())}, IsDead? {IsDead} at frame #{Stage.__Updates}");
            //    throw e;
            //}
        }
        public Vector2 GetRightMostPoint()
        {
            _tempShip = GetShips().OrderByDescending((ship) => ship.GetRightMostPoint().x).ToList().First();
            return new Vector2(_tempShip.GetRightMostPoint().x, _tempShip.GetY());
        }
        public Vector2 GetTopMostPoint()
        {
            _tempShip = GetShips().OrderByDescending((ship) => ship.GetTopMostPoint().y).ToList().First();
            return new Vector2(_tempShip.GetX(), _tempShip.GetTopMostPoint().y);
        }
        public Vector2 GetBottomMostPoint()
        {
            _tempShip = GetShips().OrderBy((ship) => ship.GetBottomMostPoint().y).ToList().First();
            return new Vector2(_tempShip.GetX(), _tempShip.GetBottomMostPoint().y);
        }
        public float GetWidth()
        {
            return Math.Abs(GetLeftMostPoint().x - GetRightMostPoint().x);
        }
        public float GetHeight()
        {
            return Math.Abs(GetTopMostPoint().y - GetBottomMostPoint().y);
        }
        float _width, _height, _midX, _midY;
        public Vector2 GetCenterPoint()
        {

            // calculate width and height of box
            _width = GetWidth();
            _height = GetHeight();

            // calculate center point of box

            _midX = GetRightMostPoint().x - (_width / 2);
            _midY = GetBottomMostPoint().y + (_height / 2);

            return new Vector2(_midX, _midY);
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
            _tempPosition = GetPosition();
            return new Vector2((_tempPosition.x + (Mathf.Cos(angle) * distance)), (_tempPosition.y + (Mathf.Sin(angle) * distance)));
        }



        // UI Methods
        public void MoveSquadBox()
        {
            //Debug.Log($"Squad #{squadNumber} is moving and the squad box will have width {GetWidth()}, height {GetHeight()}, and center point {GetCenterPoint()}");
            //Debug.Log($"Right most point {GetRightMostPoint()}, Left most point {GetLeftMostPoint()}, Top most point {GetTopMostPoint()}, Bottom most point {GetBottomMostPoint()}");
            if (IsSelected && !Stage.IsTraining && !HasMovedBox)
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
                    _tempShip = GetShips().First();
                    SquadBox.transform.eulerAngles = Vector3.forward * _tempShip.Rotation;
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

