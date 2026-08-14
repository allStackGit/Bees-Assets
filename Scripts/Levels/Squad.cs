using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad : MonoBehaviour
    {
        public Level Level;
        public Stage Stage;
        public int Side, SquadNumber;
        public ulong OpponentId;
        public long Id;
        public int ItemId;
        public long Age;
        public ConfigData.SquadTypes SquadType;

        private Command _command;
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public MatchupStrategy MatchupStrategy = new MatchupStrategy();
        public HashSet<ConfigData.CommandTypes> BannedStrats = new HashSet<ConfigData.CommandTypes>();

        public string Status;
        public string Name;
        public Color Color;
        public Color SquadBoxColor;
        public SavedSquad SavedSquad;
        public GameObject SquadBox;
        public SquadTab SquadTab;

        public bool HasMovedBox;
        public bool IsMatchingSpeed;
        public bool IsImmobile;
        public bool CeaseFire;
        public bool HasAddedShips;
        public bool IsShowingRanges;
        public bool IsGrowingSquad;
        public bool HasCustomColor;
        public bool HasSquadTab;
        public bool HasSquadBox;
        public bool IsMinionSquad;
        public bool IsDead;
        public bool IsSelected;
        public bool IsUserControlled;
        public bool IsHiveMindControlled;
        public bool IsCarrierSquad;
        public bool HasCommand;
        public bool HasCommandQueue;
        public bool CanAcceptUserInput;
        public bool IsLockedOn;

        public float CurrentSpeed;
        public Vector2 Destination;
        public Queue<Command> CommandQueue = new Queue<Command>();
        public Action CommandQueueEmptyAction;

        private readonly List<Ship> _ships = new List<Ship>();
        private readonly List<(Ship ship, Vector2 position)> _placedFormationSlots = new List<(Ship ship, Vector2 position)>();
        private bool _isInBounds;
        private bool _shouldChase;
        private ConfigData.ShootingStrategyTypes _chosenShootingStrategy;

        public int LastKilled
        {
            get
            {
                if (_ships.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
                int value = _ships[0].LastKilled;
                for (int i = 1; i < _ships.Count; i++)
                {
                    int candidate = _ships[i].LastKilled;
                    if (candidate > value) value = candidate;
                }
                return value;
            }
        }

        public int DamageDone
        {
            get
            {
                int sum = 0;
                checked
                {
                    for (int i = 0; i < _ships.Count; i++) sum += _ships[i].FleetShip.DamageDone;
                }
                return sum;
            }
        }

        public int Health
        {
            get
            {
                int sum = 0;
                checked
                {
                    for (int i = 0; i < _ships.Count; i++) sum += _ships[i].Health;
                }
                return sum;
            }
        }

        public float Firepower
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < _ships.Count; i++) sum += _ships[i].Firepower;
                return (float)sum;
            }
        }

        public int MaxRange
        {
            get
            {
                if (_ships.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
                int value = _ships[0].MaxRange;
                for (int i = 1; i < _ships.Count; i++)
                {
                    int candidate = _ships[i].MaxRange;
                    if (candidate > value) value = candidate;
                }
                return value;
            }
        }

        public int MaxSight
        {
            get
            {
                if (_ships.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
                int value = _ships[0].Sight;
                for (int i = 1; i < _ships.Count; i++)
                {
                    int candidate = _ships[i].Sight;
                    if (candidate > value) value = candidate;
                }
                return value;
            }
        }

        public float TotalSpeed
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < _ships.Count; i++) sum += _ships[i].Speed;
                return (float)sum;
            }
        }

        public float MaxSpeed
        {
            get
            {
                if (_ships.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
                float value = _ships[0].Speed;
                for (int i = 1; i < _ships.Count; i++)
                {
                    float candidate = _ships[i].Speed;
                    if (candidate > value || double.IsNaN(value)) value = candidate;
                }
                return value;
            }
        }

        public int Tsv
        {
            get
            {
                int sum = 0;
                checked
                {
                    for (int i = 0; i < _ships.Count; i++) sum += _ships[i].Tsv;
                }
                return sum;
            }
        }

        public float SlowestSpeed
        {
            get
            {
                if (_ships.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
                float value = _ships[0].Speed;
                for (int i = 1; i < _ships.Count; i++)
                {
                    float candidate = _ships[i].Speed;
                    if (candidate < value || float.IsNaN(candidate)) value = candidate;
                }
                return value;
            }
        }

        public bool HasEnemy => HasCommand && GetCommand() != null && GetCommand().HasEnemy;
        public bool IsAttacking => HasCommand && GetCommand() != null && GetCommand().IsAttacking;
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide
            ? Level.CurrentLevelOptions.UserStartingPosition
            : Level.CurrentLevelOptions.AIStartingPosition;

        public bool IsDefenseless
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].Firepower != 0) return false;
                }
                return true;
            }
        }

        public bool HasMiningShips
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].IsMiningShip) return true;
                }
                return false;
            }
        }

        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();

        public bool HasOnlyStrikers
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].ShipType != ConfigData.ShipTypes.Striker) return false;
                }
                return true;
            }
        }

        public bool HasOnlyBombers
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    ConfigData.ShipTypes type = _ships[i].ShipType;
                    if (type != ConfigData.ShipTypes.Striker &&
                        type != ConfigData.ShipTypes.YellowJacket &&
                        type != ConfigData.ShipTypes.FireBarge)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public bool HasOnlyBarges
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].ShipType != ConfigData.ShipTypes.Barge) return false;
                }
                return true;
            }
        }

        public bool HasOnlyWarpGates
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (!_ships[i].IsWarpGate) return false;
                }
                return true;
            }
        }

        public bool HasOnlyBeehives
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (!_ships[i].IsBeehive) return false;
                }
                return true;
            }
        }

        public bool HasReachedDestination
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (!_ships[i].HasReachedDestination) return false;
                }
                return true;
            }
        }

        public bool HasDestination
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].HasTargetCoordinates) return true;
                }
                return false;
            }
        }

        public bool InCombat
        {
            get
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (_ships[i].InCombat) return true;
                }
                return false;
            }
        }

        private List<Ship> _tempShips;
        private Ship _tempShip;
        private Squad _tempSquad;
        private List<Squad> _tempSquads;
        private Vector2 _tempPosition;

        public virtual void ClearData()
        {
            CancelScriptedCommandQueue();
            CanAcceptUserInput = false;
            SetCommandNull();
            PastCommands.Clear();
            BannedStrats.Clear();
            Status = "idle";
            HasMovedBox = false;
            IsImmobile = false;
            HasAddedShips = false;
            IsShowingRanges = false;
            HasSquadTab = false;
            IsGrowingSquad = false;
            HasCustomColor = false;
            _ships.Clear();
            _placedFormationSlots.Clear();
            _shouldChase = false;
            Destination = Vector2.zero;
            IsDead = false;
            CurrentSpeed = 0;
            CommandQueueEmptyAction = null;
            HasCommandQueue = false;
            IsSelected = false;
            IsLockedOn = false;
            IsUserControlled = false;
            IsHiveMindControlled = false;
            _isInBounds = false;
            enabled = true;
        }

        public virtual void Create(Stage stage)
        {
            SquadType = ConfigData.SquadTypes.Squad;
            Stage = stage;
            IsDead = true;
            enabled = false;
        }

        private readonly ScaledTimer _checkChaseTimer = new ScaledTimer();

        public void Setup(Level level, SavedSquad savedSquad, ConfigData.ShootingStrategyTypes shootingStrategy,
            bool ceaseFire, bool isMatchingSpeed, bool shouldChase, bool isImobile, long id, int side,
            int squadNumber, string name, Color color)
        {
            ClearData();
            _preserveAuthoredOffsetsOnNextSetOffsets = true;
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
            IsHiveMindControlled = !IsUserControlled;
            CanAcceptUserInput = IsUserControlled;

            if (Color != ConfigData.UnsetColor && IsUserControlled)
            {
                HasCustomColor = true;
                SquadBoxColor = new Color(Color.r, Color.g, Color.b,
                    ConfigData.GetUIColor("squadbox-default-color").a);
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
            }
            if (Stage.FullCeaseFire || (Side == ConfigData.Configuration.AISide && Stage.MakeEnemyCeaseFire))
            {
                SetSquadCeaseFire(true);
            }
        }

        public void SetSquadCeaseFire(bool ceasefire)
        {
            CeaseFire = ceasefire;
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                ships[i].IsCeaseFire = CeaseFire;
            }
        }

        private void SetSquadBox()
        {
            if (Stage.IsTraining) return;
            if (!HasSquadBox)
            {
                SquadBox = Instantiate(Stage.Prefabs.SquadBoxPrefab, Vector2.zero, Quaternion.identity);
                HasSquadBox = true;
            }
            SquadBox.transform.parent = Level.Map.Transform;
            SquadBox.SetActive(false);
            SquadBox.name = $"{Name} - Squadbox";
        }

        private void SetOpponent()
        {
            if (Side == ConfigData.Configuration.AISide)
                OpponentId = Stage.IsTraining ? 1UL : ConfigData.GetUserId();
            else if (Side == ConfigData.Configuration.UserSide)
                OpponentId = 0;
        }

        private Vector2 _adjustment;
        private readonly Vector2 _queenMultiplier = new Vector2(2.75f, 2);
        private readonly Vector2 _wideMultiplier = new Vector2(1.4f, 1);
        private readonly Vector2 _ultraWideMultiplier = new Vector2(2.5f, 1f);
        private readonly ConfigData.ShipTypes[] _wideShips =
        {
            ConfigData.ShipTypes.Barge, ConfigData.ShipTypes.FireBarge,
            ConfigData.ShipTypes.Flagship, ConfigData.ShipTypes.CarpenterBee
        };
        private readonly ConfigData.ShipTypes[] _ultraWideShips =
        {
            ConfigData.ShipTypes.Beehive, ConfigData.ShipTypes.WarpGate
        };

        private Vector2 GetFormationAdjustment(Ship ship)
        {
            Vector2 adjustment = ship.OffsetFromCenter;
            if (ship.ShipType == ConfigData.ShipTypes.Queen && GetShips().Count > 1) adjustment *= _queenMultiplier;
            else if (ship.ShipType == ConfigData.ShipTypes.Bumblebee) adjustment *= 1.2f;
            else if (_wideShips.Contains(ship.ShipType)) adjustment *= _wideMultiplier;
            else if (_ultraWideShips.Contains(ship.ShipType)) adjustment *= _ultraWideMultiplier;
            return adjustment;
        }

        private Vector2 GetFormationSlot(Ship ship, Vector2 center)
        {
            return GetShips().Count == 1 ? center : center + GetFormationAdjustment(ship);
        }

        private bool CanPlaceFormationAt(Vector2 center)
        {
            if (Level == null || Level.Pathfinder == null || !Level.HasObstacles)
            {
                return true;
            }

            foreach (Ship ship in GetShips())
            {
                if (!Level.Pathfinder.CanOccupyDestination(GetFormationSlot(ship, center), ship.GetClearance()))
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryFindNearestFormationCenter(Vector2 requestedCenter, out Vector2 center)
        {
            center = requestedCenter;
            if (GetShips().Count == 0 || CanPlaceFormationAt(requestedCenter))
            {
                return true;
            }

            int step = Pathfinder.Scale * 2;
            int maxSearchDistance = Mathf.Max(Level.MapWidth, Level.MapHeight);
            int maxRadius = Mathf.CeilToInt((float)maxSearchDistance / step);

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                Vector2 best = requestedCenter;
                float bestDistance = float.MaxValue;
                bool found = false;

                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Mathf.Abs(x) != radius && Mathf.Abs(y) != radius)
                        {
                            continue;
                        }

                        Vector2 candidate = requestedCenter + new Vector2(x * step, y * step);
                        if (!CanPlaceFormationAt(candidate))
                        {
                            continue;
                        }

                        float distance = (candidate - requestedCenter).sqrMagnitude;
                        if (!found || distance < bestDistance)
                        {
                            found = true;
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }

                if (found)
                {
                    center = best;
                    return true;
                }
            }

            return false;
        }

        private static bool WouldOverlapPlacedShip(Ship ship, Vector2 candidate, Ship placedShip, Vector2 placedPosition)
        {
            return Mathf.Abs(candidate.x - placedPosition.x) < ship.GetHalfWidth() + placedShip.GetHalfWidth() &&
                   Mathf.Abs(candidate.y - placedPosition.y) < ship.GetHalfHeight() + placedShip.GetHalfHeight();
        }

        private bool IsValidIndividualFormationSlot(Ship ship, Vector2 candidate, List<(Ship ship, Vector2 position)> placed)
        {
            if (!Level.Pathfinder.CanOccupyDestination(candidate, ship.GetClearance()))
            {
                return false;
            }

            for (int i = 0; i < placed.Count; i++)
            {
                if (WouldOverlapPlacedShip(ship, candidate, placed[i].ship, placed[i].position))
                {
                    return false;
                }
            }
            return true;
        }

        private Vector2 FindNearestIndividualFormationSlot(Ship ship, Vector2 requestedSlot, List<(Ship ship, Vector2 position)> placed)
        {
            if (Level == null || Level.Pathfinder == null || !Level.HasObstacles ||
                IsValidIndividualFormationSlot(ship, requestedSlot, placed))
            {
                return requestedSlot;
            }

            const int maxSearchDistance = 256;
            int step = Pathfinder.Scale;
            int maxRadius = Mathf.CeilToInt((float)maxSearchDistance / step);

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                Vector2 best = requestedSlot;
                float bestDistance = float.MaxValue;
                bool found = false;

                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Mathf.Abs(x) != radius && Mathf.Abs(y) != radius)
                        {
                            continue;
                        }

                        Vector2 candidate = requestedSlot + new Vector2(x * step, y * step);
                        if (!IsValidIndividualFormationSlot(ship, candidate, placed))
                        {
                            continue;
                        }

                        float distance = (candidate - requestedSlot).sqrMagnitude;
                        if (!found || distance < bestDistance)
                        {
                            found = true;
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }

                if (found)
                {
                    return best;
                }
            }

            if (Level.Pathfinder.TryFindNearestValidDestination(requestedSlot, ship.GetClearance(), out Vector2 validDestination))
            {
                return validDestination;
            }
            return requestedSlot;
        }

        private void PlaceFormationSlotsIndividually(Vector2 requestedCenter)
        {
            _placedFormationSlots.Clear();
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                Vector2 requestedSlot = GetFormationSlot(ship, requestedCenter);
                Vector2 position = FindNearestIndividualFormationSlot(ship, requestedSlot, _placedFormationSlots);
                ship.transform.localPosition = position;
                _placedFormationSlots.Add((ship, position));
            }
        }

        public void SetStartingPosition(Vector2 position)
        {
            List<Ship> ships = GetShips();
            if (TryFindNearestFormationCenter(position, out Vector2 formationCenter))
            {
                if (ships.Count == 1)
                {
                    ships[0].transform.localPosition = formationCenter;
                    return;
                }

                for (int i = 0; i < ships.Count; i++)
                {
                    Ship ship = ships[i];
                    _adjustment = GetFormationAdjustment(ship);
                    ship.transform.localPosition = new Vector2(formationCenter.x + _adjustment.x, formationCenter.y + _adjustment.y);
                }
                return;
            }

            PlaceFormationSlotsIndividually(position);
        }

        public void SetSquadTab()
        {
            if (!IsUserControlled || SquadNumber <= 0 || SquadNumber > 10) return;
            SquadTab = Stage.SquadTabs[SquadNumber - 1];
            HasSquadTab = true;
            if (HasCustomColor) SquadTab.SetColor(Color);
            SquadTab.ShowTab();
        }

        public bool CanBeSelected() => IsUserControlled && CanAcceptUserInput && !Level.State.SelectedSquads.Contains(this);

        public void NameSquadShips()
        {
            foreach (Ship ship in GetShips()) ship.SetSquadName();
        }

        public void FixedUpdate()
        {
            if (IsUserControlled) HasMovedBox = false;
        }

        public List<Ship> GetShips() => _ships;

        public void AddShip(Ship ship)
        {
            _ships.Add(ship);
            RefreshCompositionCommandBans();
            HasAddedShips = true;
        }

        public void RemoveShip(Ship ship)
        {
            _ships.Remove(ship);
            RefreshCompositionCommandBans();
            if (IsSelected && !Stage.IsTraining) Stage.Menus.ActionBox.SetSquadsText();
        }

        public override string ToString() => $"Squad {Name} (#{ItemId}) with {_ships.Count} ships ({(IsDead ? "D" : "A")})";

        public override bool Equals(object obj)
        {
            Squad other = obj as Squad;
            if (ReferenceEquals(other, null) || (UnityEngine.Object)other == null) return false;
            return ItemId == other.ItemId;
        }

        public bool Equals(Squad other) => !ReferenceEquals(other, null) && (UnityEngine.Object)other != null && ItemId == other.ItemId;
        public override int GetHashCode() => ItemId.GetHashCode();

        public static bool operator ==(Squad a, Squad b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (ReferenceEquals(a, null) || ReferenceEquals(b, null)) return false;
            if ((UnityEngine.Object)a == null || (UnityEngine.Object)b == null) return false;
            return a.ItemId == b.ItemId;
        }

        public static bool operator !=(Squad a, Squad b) => !(a == b);
    }
}