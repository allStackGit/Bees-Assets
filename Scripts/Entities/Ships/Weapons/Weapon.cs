

using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Weapon : MonoBehaviour
    {

        public Ship Ship;
        public Ship TargetShip;
        public int Range, Power;
        public float RateOfFire, ProjectileValue, RotationRate, SpecialFirepower, Firepower;
        public GameObject Piece, RangeCircle;
        public ConfigData.ProjectileTypes ProjectileType;
        public List<Ship> CachedTargetingQueue = new List<Ship>();
        public Dictionary<long, Ship> ShipsWithinRange = new Dictionary<long, Ship>();
        public ConfigData.ShootingStrategyTypes CachedShootingStrategy;
        public string Name;
        public ConfigData.WeaponTypes Type;
        public ConfigData.WeaponSoundTypes WeaponSoundType;
        public bool IsUsingCachedTargetingQueue, HasCachedChanged, HasSoundEffect;
        public AudioSource SoundEffect;
        public bool HasTargetShip => TargetShip != null;
        public int Id, Side;
        public Level Level;
        public Stage Stage;
        public bool HasRangeCircle, HasRangeCollider, HasSpriteRenderer;
        public RangeCollider RangeCollider;
        public SpriteRenderer SpriteRenderer;
        public Transform PieceTransform;
        public virtual bool ShouldFire => TargetShip != null && !TargetShip.IsDead && !Ship.IsCeaseFire;

        private Comparison<Ship> _compareClosestTargets;
        private Comparison<Ship> _compareFurthestTargets;
        private Comparison<Ship> _comparePreferredTargetType;
        private ConfigData.ShipTypeLetters _preferredTargetType;

        public virtual void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float specialFirePower, float rateOfFire, float projectileValue, GameObject piece,
            ConfigData.ProjectileTypes projectileType)
        {
            Ship = ship;
            Side = Ship.Side;
            Stage = Ship.Stage;
            Range = range;
            Power = power;
            SpecialFirepower = specialFirePower;
            ProjectileValue = projectileValue;
            RateOfFire = rateOfFire;
            Piece = piece;
            PieceTransform = Piece.transform;
            _compareClosestTargets ??= CompareClosestTargets;
            _compareFurthestTargets ??= CompareFurthestTargets;
            _comparePreferredTargetType ??= ComparePreferredTargetType;
            WeaponsData weaponsData = Piece.GetComponent<WeaponsData>();
            SpriteRenderer = weaponsData.SpriteRenderer;
            if (SpriteRenderer != null && Stage.IsRendering) HasSpriteRenderer = true;
            Destroy(weaponsData);
            ProjectileType = projectileType;
            Type = type;
            WeaponSoundType = weaponSound;

            if (!Stage.IsTraining && Stage.Audio.WeaponSounds.ContainsKey(WeaponSoundType))
            {
                HasSoundEffect = true;
                SoundEffect = Instantiate(Stage.Audio.WeaponSounds[WeaponSoundType][Utilities.RandomInt(Stage.Audio.WeaponSounds[WeaponSoundType].Length)]);
                SoundEffect.transform.parent = PieceTransform;
                SoundEffect.transform.localPosition = Vector2.zero;
            }
            Firepower = Utilities.CalculateFirepower(Power, Range, RateOfFire, RotationRate, ProjectileValue, SpecialFirepower);
            SetupRangeCircleAndCollider();
            if (!Stage.IsRendering) Destroy(SpriteRenderer);
        }

        public virtual void Setup()
        {
            Level = Ship.Level;
            Id = Level.State.GetId();
            Name = $"{Ship.Name}: {Type}";
            ClearData();
            Activate();
        }

        public virtual void ClearData()
        {
            TargetShip = null;
            CachedTargetingQueue.Clear();
            ShipsWithinRange.Clear();
            _targetDistanceKeys.Clear();
            _enemyTargetBuffer.Clear();
            _disregardRangeBuffer.Clear();
            IsUsingCachedTargetingQueue = false;
            HasCachedChanged = false;
        }

        public virtual void CancelTimer() { }

        public virtual void Activate()
        {
            if (HasRangeCollider) RangeCollider.Activate();
            if (HasSpriteRenderer) SpriteRenderer.enabled = true;
            enabled = true;
        }

        public virtual void Deactivate()
        {
            if (HasRangeCollider) RangeCollider.Deactivate();
            if (HasSpriteRenderer) SpriteRenderer.enabled = false;
            enabled = false;
        }

        public void SetupRangeCircleAndCollider()
        {
            Transform rangeCircle = PieceTransform.Find("Range Circle");
            Transform rangeColliderTransform = PieceTransform.Find("Range Collider");
            if (Ship.IsUserControlled && rangeCircle != null)
            {
                RangeCircle = rangeCircle.gameObject;
                RangeCircle.transform.localScale = new Vector3(Range * 2, Range * 2, 0);
                HasRangeCircle = true;
            }
            else if (rangeCircle != null) Destroy(rangeCircle.gameObject);
            if (rangeColliderTransform != null)
            {
                RangeCollider rangeCollider = rangeColliderTransform.GetComponent<RangeCollider>();
                RangeCollider = rangeCollider;
                if (Ship.ShipType == ConfigData.ShipTypes.Frigate) RangeCollider.Create(this, Range - 7);
                else RangeCollider.Create(this, Range);
            }
        }

        protected virtual void SetTargetShip(Ship targetShip) { TargetShip = targetShip; }
        private bool _foundTarget;
        private int _index;
        private Ship _potentialTargetShip;
        private ShipDamageStatus _shipDamageStatus;

        public bool DetermineTargetShip(List<Ship> ships, bool useShipDamageStatus)
        {
            _foundTarget = false;
            for (_index = 0; _index < ships.Count; _index++)
            {
                _potentialTargetShip = ships[_index];
                if (!_foundTarget && IsShipValidTarget(_potentialTargetShip))
                {
                    if (useShipDamageStatus)
                    {
                        _shipDamageStatus = Level.State.GetShipDamageStatus(Side, _potentialTargetShip);
                        if (_shipDamageStatus.TotalDamageSentToShip < _shipDamageStatus.Health)
                        {
                            SetTargetShip(_potentialTargetShip);
                            _foundTarget = true;
                            return _foundTarget;
                        }
                    }
                    else
                    {
                        SetTargetShip(_potentialTargetShip);
                        _foundTarget = true;
                        return _foundTarget;
                    }
                }
            }
            if (ships.Count == 0) CachedTargetingQueue.Clear();
            return _foundTarget;
        }

        public virtual bool IsShipValidTarget(Ship potentialTargetShip) => !potentialTargetShip.IsDead && IsShipWithinRange(potentialTargetShip);
        private List<Ship> _queue;

        public void Targeting()
        {
            TargetShip = null;
            if (Ship.IsUserControlled || Ship.Squad.HasCommand || Ship.HasBrain)
            {
                _queue = MakeSortedTargetingList(false);
                if (!DetermineTargetShip(_queue, true)) DetermineTargetShip(_queue, false);
            }
        }

        private readonly List<Ship> _enemyTargetBuffer = new List<Ship>();
        public List<Ship> GetEnemyShipsWithinRange()
        {
            _enemyTargetBuffer.Clear();
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                Squad enemySquad = Ship.Squad.GetCommand().EnemySquad;
                foreach (Ship candidate in ShipsWithinRange.Values)
                {
                    if (candidate.Squad == enemySquad)
                    {
                        _enemyTargetBuffer.Add(candidate);
                    }
                }
                if (_enemyTargetBuffer.Count > 0)
                {
                    return _enemyTargetBuffer;
                }
            }

            _enemyTargetBuffer.Clear();
            foreach (Ship candidate in ShipsWithinRange.Values)
            {
                _enemyTargetBuffer.Add(candidate);
            }
            return _enemyTargetBuffer;
        }

        private readonly List<Ship> _disregardRangeBuffer = new List<Ship>();
        protected virtual List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }

            if (disregardRange)
            {
                _disregardRangeBuffer.Clear();
                _disregardRangeBuffer.AddRange(Ship.Squad.GetCommand().EnemySquad.GetShips());
                _queue = _disregardRangeBuffer;
            }
            else
            {
                _queue = GetEnemyShipsWithinRange();
            }
            IsUsingCachedTargetingQueue = false;
            return _queue;
        }

        public void ClearTargets()
        {
            TargetShip = null;
            CachedTargetingQueue.Clear();
            HasCachedChanged = true;
        }

        private List<Ship> _sortedQueue;
        private readonly Dictionary<long, float> _targetDistanceKeys = new Dictionary<long, float>();
        private void CacheTargetDistances()
        {
            _targetDistanceKeys.Clear();
            foreach (Ship target in _sortedQueue)
            {
                _targetDistanceKeys[target.Id] = DistanceTo(target);
            }
        }

        private int CompareClosestTargets(Ship a, Ship b)
        {
            return _targetDistanceKeys[a.Id].CompareTo(_targetDistanceKeys[b.Id]);
        }

        private int CompareFurthestTargets(Ship a, Ship b)
        {
            return _targetDistanceKeys[b.Id].CompareTo(_targetDistanceKeys[a.Id]);
        }

        private int ComparePreferredTargetType(Ship a, Ship b)
        {
            if (a.ShipTypeLetter == _preferredTargetType && b.ShipTypeLetter != _preferredTargetType) return -1;
            if (b.ShipTypeLetter == _preferredTargetType && a.ShipTypeLetter != _preferredTargetType) return 1;
            return 0;
        }

        public List<Ship> MakeSortedTargetingList(bool disregardRange)
        {
            _sortedQueue = GetPotentialEnemyTargetShips(disregardRange);
            ConfigData.ShootingStrategyTypes strategy = Ship.ShootingStrategy;
            CachedShootingStrategy = strategy;
            CachedTargetingQueue = _sortedQueue;
            HasCachedChanged = false;
            if (!IsUsingCachedTargetingQueue)
            {
                switch (strategy)
                {
                    case ConfigData.ShootingStrategyTypes.FirstSeen: return _sortedQueue;
                    case ConfigData.ShootingStrategyTypes.Random: _sortedQueue.Shuffle(); break;
                    case ConfigData.ShootingStrategyTypes.Revenge: _sortedQueue.Sort((a, b) => b.LastKilled.CompareTo(a.LastKilled)); break;
                    case ConfigData.ShootingStrategyTypes.MostDangerous: _sortedQueue.Sort((a, b) => b.FleetShip.DamageDone.CompareTo(a.FleetShip.DamageDone)); break;
                    case ConfigData.ShootingStrategyTypes.LeastHealth: _sortedQueue.Sort((a, b) => a.Health.CompareTo(b.Health)); break;
                    case ConfigData.ShootingStrategyTypes.MostHealth: _sortedQueue.Sort((a, b) => b.Health.CompareTo(a.Health)); break;
                    case ConfigData.ShootingStrategyTypes.MostPowerful: _sortedQueue.Sort((a, b) => b.Firepower.CompareTo(a.Firepower)); break;
                    case ConfigData.ShootingStrategyTypes.LeastPowerful: _sortedQueue.Sort((a, b) => a.Firepower.CompareTo(b.Firepower)); break;
                    case ConfigData.ShootingStrategyTypes.Closest:
                        CacheTargetDistances();
                        _sortedQueue.Sort(_compareClosestTargets);
                        break;
                    case ConfigData.ShootingStrategyTypes.Furthest:
                        CacheTargetDistances();
                        _sortedQueue.Sort(_compareFurthestTargets);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostRange: _sortedQueue.Sort((a, b) => b.MaxRange.CompareTo(a.MaxRange)); break;
                    case ConfigData.ShootingStrategyTypes.LeastRange: _sortedQueue.Sort((a, b) => a.MaxRange.CompareTo(b.MaxRange)); break;
                    case ConfigData.ShootingStrategyTypes.Fastest: _sortedQueue.Sort((a, b) => b.Speed.CompareTo(a.Speed)); break;
                    case ConfigData.ShootingStrategyTypes.Slowest: _sortedQueue.Sort((a, b) => a.Speed.CompareTo(b.Speed)); break;
                    case ConfigData.ShootingStrategyTypes.MostValuable: _sortedQueue.Sort((a, b) => b.Tsv.CompareTo(a.Tsv)); break;
                    case ConfigData.ShootingStrategyTypes.LeastValuable: _sortedQueue.Sort((a, b) => a.Tsv.CompareTo(b.Tsv)); break;
                    default:
                        if ((int) strategy > 15)
                        {
                            _preferredTargetType = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                            _sortedQueue.Sort(_comparePreferredTargetType);
                        }
                        break;
                }
            }
            return _sortedQueue;
        }

        protected virtual void SendProjectile()
        {
            if (HasTargetShip) Level.State.GetShipDamageStatus(Side, TargetShip).TotalDamageSentToShip += Power;
            PlaySoundEffect();
        }
        protected void PlaySoundEffect() { if (HasSoundEffect) SoundEffect.Play(); }
        public void StopSoundEffect() { if (HasSoundEffect) SoundEffect.Stop(); }
        public bool IsShipWithinRange(Ship ship) => ShipsWithinRange.ContainsKey(ship.Id);
        public virtual bool IsPointWithinRange(Vector2 point) => DistanceToPoint(point) <= Range;
        public float DistanceToPoint(Vector2 point) => Vector2.Distance(GetPosition(), point);
        public float DistanceTo(Entity entity) => DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()));
        public virtual Vector2 GetPosition() => Ship.GetPosition();
        public float AngleToPoint(Vector2 point) => Utilities.AngleBetweenPoints(GetPosition(), point);
        private Vector2 _direction;
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            _direction = point - GetPosition();
            return Mathf.Repeat(-Mathf.Atan2(_direction.x, _direction.y) * Mathf.Rad2Deg, 360f);
        }
        public bool Equals(Weapon weapon) => weapon.Id == Id;
        public override int GetHashCode() => Id;
        public virtual void ShowRange() { if (HasRangeCircle) RangeCircle.SetActive(true); }
        public virtual void HideRange() { if (HasRangeCircle) RangeCircle.SetActive(false); }
    }
}