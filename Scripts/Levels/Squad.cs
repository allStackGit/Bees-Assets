using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using static UnityEngine.EventSystems.EventTrigger;

namespace Assets.Scripts.Levels
{
    public class Squad : MonoBehaviour
    {
        public Level Level;
        public Stage Stage;
        public int Side, SquadNumber;
        public ulong OpponentId;
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
        public bool HasMovedBox, IsMatchingSpeed, IsImmobile, CeaseFire, HasAddedShips, IsShowingRanges, IsGrowingSquad, HasCustomColor, HasSquadTab, HasSquadBox, IsMinionSquad;
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
        /// <summary>
        /// Represents a queue of commands to be processed.
        /// </summary>
        /// <remarks>When a user controlled or AI controlled squad needs specific override commands for campaign reasons, this is used.</remarks>
        public Queue<Command> CommandQueue = new Queue<Command>();
        /// <summary>
        /// If there is a command queue, this is the action that will be triggered when the queue is empty, it's often used to refill the queue
        /// </summary>
        public Action CommandQueueEmptyAction;
        public bool HasCommandQueue;
        /// <summary>
        /// If this is false, the squad will not respond to user input. Only important for user squads. Usually used with the command queue
        /// </summary>
        public bool CanAcceptUserInput;
        /// <summary>
        /// Whether or not a squad is locked onto their targets. If a squad is locked on they will attack their targets until they or their targets are dead even if the player tries to move them away.
        /// </summary>
        public bool IsLockedOn;

        private List<Ship> _ships = new List<Ship>();
        private bool _isInBounds;
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
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide ? Level.CurrentLevelOptions.UserStartingPosition : Level.CurrentLevelOptions.AIStartingPosition;
        public bool IsDefenseless => GetShips().All((s) => s.Firepower == 0);
        public bool HasMiningShips => GetShips().Any((s) => s.IsMiningShip);
        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();
        public bool HasOnlyStrikers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker);
        public bool HasOnlyBombers => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Striker || s.ShipType == ConfigData.ShipTypes.YellowJacket || 
        s.ShipType == ConfigData.ShipTypes.FireBarge);
        public bool HasOnlyBarges => GetShips().All((s) => s.ShipType == ConfigData.ShipTypes.Barge);
        /// <summary>
        /// Whether or not the squad's ships are all warp gates.
        /// </summary>
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
            CanAcceptUserInput = false;
            SetCommandNull();
            HasCommand = false;
            CommandQueue.Clear();
            PastCommands.Clear();
            BannedStrats.Clear();
            Status = "idle";
            HasMovedBox = false;
            IsImmobile = false;
            HasAddedShips = false;
            IsShowingRanges = false;
            HasSquadTab = false;
            // SquadBox is a reusable child owned by this pooled Squad. Do not reset
            // HasSquadBox here or every Setup will instantiate and abandon another box.
            IsGrowingSquad = false;
            HasCustomColor = false;
            _ships.Clear();
            _shouldChase = false;
            Destination = Vector2.zero;
            IsDead = false;
            CurrentSpeed = 0;
            //MatchupStrategy.Kill();
            enabled = true;
            CommandQueueEmptyAction = null;
            HasCommandQueue = false;
            IsSelected = false;
            _isInBounds = false;

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
        public void Setup(Level level, SavedSquad savedSquad, ConfigData.ShootingStrategyTypes shootingStrategy, bool ceaseFire, bool isMatchingSpeed, bool shouldChase, bool isImobile, long id, int side, int squadNumber, string name, Color color)
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
                CanAcceptUserInput = true;
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
        private Vector2 _ultraWideMultiplier = new Vector2(2.5f, 1f);
        private ConfigData.ShipTypes[] _wideShips = new ConfigData.ShipTypes[] { ConfigData.ShipTypes.Barge, ConfigData.ShipTypes.FireBarge, ConfigData.ShipTypes.Flagship, ConfigData.ShipTypes.CarpenterBee };
        private ConfigData.ShipTypes[] _ultraWideShips = new ConfigData.ShipTypes[] { ConfigData.ShipTypes.Beehive, ConfigData.ShipTypes.WarpGate };
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
            if (GetShips().Count == 1)
            {
                GetShips()[0].transform.localPosition = position;
            }
            else
            {
                GetShips().ForEach((ship) =>
                {
                    // The size factor (1-16)
                    //Vector2 sizeFactor = (largestShipSize / ConfigData.DragIconSize) * ConfigData.WorldUnitScaleFactor;


                    // trying to place ships on the map according to where they were in the squad maker
                    // Option 1: Convert the squadmaker coordinates directly to map coordinates

                    //Debug.Log($"Ship: {ship.Name} Position: {position}, Offset from Center: {ship.OffsetFromCenter}");

                    _adjustment = ship.OffsetFromCenter;

                    if (ship.ShipType == ConfigData.ShipTypes.Queen && GetShips().Count > 1)
                    {
                        _adjustment *= _queenMultiplier; // Need larger spacing between the Queen(s) because it's so large
                    }
                    else if (ship.ShipType == ConfigData.ShipTypes.Bumblebee)
                    {
                        _adjustment *= 1.2f;
                    }
                    else if (_wideShips.Contains(ship.ShipType))
                    {
                        _adjustment *= _wideMultiplier;
                    }
                    else if (_ultraWideShips.Contains(ship.ShipType))
                    {
                        _adjustment *= _ultraWideMultiplier;
                    }

                    //Debug.Log($"Sizefactor for {ship.Name}: {sizeFactor}");
                    //ship.transform.localPosition = Level.ForceBounds((position.x + adjustment.x), (position.y + adjustment.y));
                    ship.transform.localPosition = new Vector2(position.x + _adjustment.x, position.y + _adjustment.y);
                    //Debug.Log($"Local starting position for {ship.Name}: {ship.transform.localPosition}");

                });
            }
                

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
        /// <summary>
        /// Whether this squad can be selected. It must be user controlled, be able to accept user input, and not be already selected
        /// </summary>
        /// <returns></returns>
        public bool CanBeSelected()
        {
            return IsUserControlled && CanAcceptUserInput && !Level.State.SelectedSquads.Contains(this);
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
            // Existing debug-only Update body intentionally remains commented out.
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
        private const int FormationCompressionSteps = 20;
        public void StopMoving()
        {
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                if (ship.IsMobile)
                {
                    ship.StopMoving("Squad ordered to stop");
                }
            }
        }
        public void Move(Vector2 destination)
        {
            if (IsSelected && Level.Stage.Menus.HasSquadActionBox)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
            _tempShips = GetShips().Where(ship => ship.IsMobile).ToList();
            Vector2 formationCenter = Level.ForceBounds(destination);
            float formationCompression = 1f;

            if (Level.HasObstacles && _tempShips.Count > 0 && !TryGetFormationCompression(formationCenter, _tempShips, out formationCompression))
            {
                int largestClearance = _tempShips.Max(ship => ship.GetClearance());
                if (!Level.Pathfinder.TryFindNearestValidDestination(formationCenter, largestClearance, out formationCenter) ||
                    !TryGetFormationCompression(formationCenter, _tempShips, out formationCompression))
                {
                    return;
                }
            }

            foreach (Ship ship in _tempShips)
            {
                Vector2 shipDestination = Level.ForceBounds(formationCenter + (ship.OffsetFromCenter * formationCompression));
                ship.MoveToPoint(shipDestination);
            }
            Destination = formationCenter;
        }
        private bool TryGetFormationCompression(Vector2 formationCenter, List<Ship> ships, out float compression)
        {
            for (int step = 0; step <= FormationCompressionSteps; step++)
            {
                float candidateCompression = 1f - ((float)step / FormationCompressionSteps);
                bool allDestinationsValid = true;
                for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                {
                    Ship ship = ships[shipIndex];
                    Vector2 candidate = Level.ForceBounds(formationCenter + (ship.OffsetFromCenter * candidateCompression));
                    if (!Level.Pathfinder.CanOccupyDestination(candidate, ship.GetClearance()))
                    {
                        allDestinationsValid = false;
                        break;
                    }
                }

                if (allDestinationsValid)
                {
                    compression = candidateCompression;
                    return true;
                }
            }

            compression = 0f;
            return false;
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
            if (_shouldChase && GetCommand()?.CommandType != ConfigData.CommandTypes.Aggressive && !Level.State.GameOver)
            {
                _tempSquad = GetClosestEnemySquad();
                if (_tempSquad != null && CanSeeSquad(_tempSquad))
                {
                    UserAggressive(_tempSquad);
                }
            }
        }
        public void Kill(bool endKill = false)
        {
            if (!IsDead)
            {
                IsDead = true;

                if (!endKill)
                {
                    if (IsUserControlled)
                    {
                        DeactivateSquadBox();
                    }

                    if (Level.State.IsSideKilled(Side) && (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign || ConfigData.IsTestingLevel))
                    {
                        Level.State.GameOver = true;
                    }
                }

                if (HasCommand)
                {
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
            }
        }
        public Squad GetClosestEnemySquad()
        {
            return Level.State.GetSquadsVisibleToHiveMind(Side).OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public Squad GetClosestValidFriendlySquad()
        {
            _tempSquads = Level.State.GetSquadsBySide(Side).Where(squad => squad != this && (!squad.HasCommand || squad.GetCommand().CommandType != ConfigData.CommandTypes.ClosestFriendly)).ToList();
            return _tempSquads.OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
        }
        public List<Ship> GetEnemyShips()
        {
            return Level.State.GetShipsVisibleToHiveMind(Side).ToList();
        }
        public List<Ship> GetFriendlyShips()
        {
            return Level.State.GetShips(Side);
        }
        private List<Ship> _enemies;
        public List<Ship> GetPotentialEnemies(Squad target)
        {
            _tempShips = GetEnemyShips();
            _enemies = _tempShips.Where((s) => s.Squad == target).Take(64).ToList();

            foreach (Ship potentialEnemy in _tempShips)
            {
                if (potentialEnemy.Squad != target && _enemies.Count < 64)
                {
                    if (potentialEnemy.IsAnySquadShipWithinRange(this))
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
            _tempShips.Clear();
            _allies = GetFriendlyShips();
            _limit = Math.Max(0, 64 - GetShipsForMatchup().Count);

            foreach (Ship potentialAlly in _allies)
            {
                if (this != potentialAlly.Squad && _tempShips.Count < _limit)
                {
                    if (potentialAlly.IsAnySquadShipWithinRange(target))
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
            Debug.Log($"Setting {this} Command to {command}");
            _command = command;
            ResetCommandCache();
        }
        public void SetCommandNull()
        {
            _command = null;
        }
        public void AddToCommandList()
        {
            Debug.Log($"Adding {this} to squads awaiting hive mind commands");
            Level.State.AddToSquadsAwaitingHiveMindCommands(this);
        }
        public bool IsInBounds()
        {
            if (!_isInBounds)
            {
                _isInBounds = GetShips().All((s) => s.IsInBounds());
            }
            return _isInBounds;
        }
        public void RunCommandQueue()
        {
            if (!IsDead)
            {
                if (CommandQueue.Count > 0)
                {
                    Command nextCommand = CommandQueue.Dequeue();
                    SetCommand(nextCommand);
                    if (GetCommand().CommandType == ConfigData.CommandTypes.MoveToPoint)
                    {
                        ((MoveToPoint)GetCommand()).Execute(GetShootingStrategy(), 0, 0);
                    }
                    else if (GetCommand().CommandType == ConfigData.CommandTypes.MoveToRandom)
                    {
                        ((MoveToRandom)GetCommand()).Execute(GetShootingStrategy(), 0, 0);
                    }
                    else if (GetCommand().CommandType == ConfigData.CommandTypes.Aggressive)
                    {
                        ((Aggressive)GetCommand()).Execute(GetShootingStrategy(), 0, 0);
                    }
                    HasCommand = true;

                }
                else if (HasCommandQueue)
                {
                    CommandQueueEmptyAction();
                }
                else if (IsHiveMindControlled && !IsImmobile)
                {
                    AddToCommandList();
                }
            }
        }
        private HashSet<ConfigData.ShipTypes> _banned, _enemyShips;
        private string[] _bannedTypes;
        public List<Ship> GetShipsForMatchup()
        {
            return GetShips().Take(64).ToList();
        }
        public void MakeMatchupStrat()
        {
            _banned = ConfigData.UserProgressData.AllShipTypes;

            if (Side == ConfigData.Configuration.BeeSide)
            {
                _enemyShips = Level.State.GetHumanShipTypes();
                _banned = _banned.Where((type) => !_enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                _enemyShips = Level.State.GetBeeShipTypes();
                _banned = _banned.Where((type) => !_enemyShips.Contains(type)).ToHashSet();
            }

            _bannedTypes = _banned.Select((ship) => $"Type {(Utilities.ConvertShipTypeToCharacter[ship])}").ToArray();
            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(new GetMatchupStrategy(AddToMatchup(GetShipsForMatchup()), OpponentId, _bannedTypes),
                this, Level, ConfigData.StandardMaxTimeOnQueue));
        }
        private static char[] _letters;
        public static string AddToMatchup(List<Ship> ships)
        {
            _letters = ships.Select(s => Utilities.ConvertShipTypeLetterToCharacter[s.ShipTypeLetter]).ToArray();
            Array.Sort(_letters);
            return new string(_letters);
        }
        private string _matchup;
        private StringBuilder _sb = new StringBuilder();
        private HashSet<ConfigData.CommandTypes> _bannedStrats;
        private int _comparativeHealth, _friendlySquadCount, _closestFriendlySquadCount;
        private List<Ship> _matchupAllies;
        private List<Ship> _matchupEnemies;
        private List<Ship> _matchupFriendlyHealthShips = new List<Ship>();

        private static double GetAverageHealthPercentForMatchup(List<Ship> ships)
        {
            if (ships == null || ships.Count == 0)
            {
                return 0d;
            }

            return ships.Average(ship => ship.OriginalHealth > 0
                ? ((double)ship.Health / ship.OriginalHealth) * 100d
                : 0d);
        }

        public void MakeMatchupAndGetCommand(Squad enemy = null)
        {
            _bannedStrats = BannedStrats.ToHashSet();

            _sb = _sb.Clear();
            _sb.Append(AddToMatchup(GetShipsForMatchup()));

            if (enemy != null)
            {
                _matchupEnemies = GetPotentialEnemies(enemy);
                if (_matchupEnemies.Count == 0)
                {
                    AddToCommandList();
                    return;
                }
                _matchupAllies = GetPotentialAllies(enemy);

                // Comparative health represents this squad plus the nearby allied ships
                // encoded in the same matchup side, not only the optional extra allies.
                _matchupFriendlyHealthShips.Clear();
                _matchupFriendlyHealthShips.AddRange(GetShipsForMatchup());
                _matchupFriendlyHealthShips.AddRange(_matchupAllies.Take(Math.Max(0, 64 - _matchupFriendlyHealthShips.Count)));
                double friendlyHealth = GetAverageHealthPercentForMatchup(_matchupFriendlyHealthShips);
                double enemyHealth = GetAverageHealthPercentForMatchup(_matchupEnemies);
                _comparativeHealth = enemyHealth <= 0d
                    ? 165
                    : (int)Math.Round((friendlyHealth / enemyHealth) * 100d);

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

                _sb.Append("||0|0");
            }

            _matchup = _sb.ToString();

            _closestFriendlySquadCount = Level.State.GetSquadsBySide(Side).Where((squad) => squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.ClosestFriendly).Count();
            _friendlySquadCount = Level.State.GetSquadsBySide(Side).Count;
            if (_friendlySquadCount - 1  <= _closestFriendlySquadCount)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.ClosestFriendly);
            }
            if (!BannedStrats.Contains(ConfigData.CommandTypes.Mining) && (!Level.ActivateMining || !HasMiningShips || GetNearestMiningAsteroid() == null))
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
            else if (Level.State.GetBeeShips().Where((s) => s.IsBeehive && ((Beehive)s).ShipsHealingHere.Count < 4).Count() == 0)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Heal);
            }

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
            if (CanAcceptUserInput)
            {
                MakeUserCommand(ConfigData.CommandTypes.Guard, null);
                ((Guard)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, squad);

                if (Level.Stage.DoesUserHaveController)
                {
                    Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
                }
            }
        }
        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            if (CanAcceptUserInput)
            {
                MakeUserCommand(ConfigData.CommandTypes.Patrol, null);
                ((Patrol)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, topLeft, bottomRight);
                if (Level.Stage.DoesUserHaveController)
                {
                    Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
                }
            }
        }
        public void UserMining(MiningAsteroid miningAsteroid)
        {
            if (CanAcceptUserInput)
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
        }
        public void UserFullRetreat(WarpGate warpGate)
        {
            if (CanAcceptUserInput)
            {
                MakeUserCommand(ConfigData.CommandTypes.FullRetreat, null);
                ((FullRetreat)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, warpGate);
            }
        }
        public void UserHeal(List<Beehive> beehives)
        {
            if (CanAcceptUserInput)
            {
                MakeUserCommand(ConfigData.CommandTypes.Heal, null);
                ((Heal)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, beehives);
            }
        }
        public void UserAggressive(Squad enemy)
        {
            if (CanAcceptUserInput)
            {
                if (HasOnlyBombers)
                {
                    UserBombingRun(enemy);
                    return;
                }
                MakeUserCommand(ConfigData.CommandTypes.Aggressive, enemy);
                ((Aggressive)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);
                MarkTargets(enemy);
            }
        }
        public void MarkTargets(Squad enemy)
        {
            if (IsUserControlled)
            {
                enemy.GetShips().ForEach((enemyShip) =>
                {
                    GameObject targetingMarker = Instantiate(Stage.Prefabs.TargetingSquadPrefab, enemyShip.transform);
                    targetingMarker.transform.localPosition = Vector2.zero;
                    targetingMarker.GetComponent<TargetingSquadMarker>().Setup(enemyShip);
                });
            }
        }
        public void UserBombingRun(Squad enemy)
        {
            MakeUserCommand(ConfigData.CommandTypes.BombingRun, enemy);
            ((BombingRun)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);
            MarkTargets(enemy);
        }
        public void MakeUserCommand(ConfigData.CommandTypes command, Squad enemy)
        {
            Level.RecordSimulationInput(
                "user-command",
                $"{ItemId}|{command}|{(enemy == null ? -1 : enemy.ItemId)}");
            FinalizeUserCommand();

            switch (command)
            {
                case ConfigData.CommandTypes.Aggressive:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive));
                    break;
                case ConfigData.CommandTypes.BombingRun:
                    SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun));
                    break;
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
            if (HasCommand || HasCommandQueue)
            {
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
            return $"Squad {Name} (#{ItemId}) with {_ships.Count} ships ({(IsDead ? "D" : "A")})";
        }
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }
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
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }
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
        public bool AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().All((s) => s.IsAnySquadShipWithinRange(squad));
        }
        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }
        public Vector2 GetPosition()
        {
            return GetCenterPoint();
        }
        public Vector2 GetLeftMostPoint()
        {
            _tempShip = GetShips().OrderBy((ship) => ship.GetLeftMostPoint().x).ToList().First();
            return new Vector2(_tempShip.GetLeftMostPoint().x, _tempShip.GetY());
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
            _width = GetWidth();
            _height = GetHeight();
            _midX = GetRightMostPoint().x - (_width / 2);
            _midY = GetBottomMostPoint().y + (_height / 2);
            return new Vector2(_midX, _midY);
        }
        public float AngleToPoint(Vector2 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
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
