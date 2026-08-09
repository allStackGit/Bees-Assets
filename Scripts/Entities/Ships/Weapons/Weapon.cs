

using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Weapon : MonoBehaviour
    {

        public Ship Ship;
        /// <summary>
        /// The ship that this weapon is currently targeting/aiming at. 
        /// </summary>
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
        /// <summary>
        /// Ships that this weapon can't fire at because an obstacle is in the way
        /// </summary>
        //public Dictionary<Ship, string> __TargetingRejectReasons = new Dictionary<Ship, string>();
        public bool HasTargetShip => TargetShip != null;
        public int Id, Side;
        public Level Level;
        public Stage Stage;
        public bool HasRangeCircle, HasRangeCollider, HasSpriteRenderer;
        public RangeCollider RangeCollider;
        public SpriteRenderer SpriteRenderer;
        public Transform PieceTransform;

        //public string __NotShootingReason;
        //public List<Ship> __ShipsWithinRange;

        /// <summary>
        /// Whether a weapon has a target ship and is not cease fire and therefore *should* fire at a target. It may still not be *able* to fire at a target, if for instance it's a turret and not aimed at the target.
        /// </summary>
        public virtual bool ShouldFire => TargetShip != null && !TargetShip.IsDead && !Ship.IsCeaseFire;

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
            WeaponsData weaponsData = Piece.GetComponent<WeaponsData>();
            SpriteRenderer = weaponsData.SpriteRenderer;
            if (SpriteRenderer != null && Stage.IsRendering)
            {
                HasSpriteRenderer = true;
            }
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

            if (!Stage.IsRendering)
            {
                Destroy(SpriteRenderer);
            }
        }

        /// <summary>
        /// Sets the weapon up for the level, clears out any old data
        /// </summary>
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
            IsUsingCachedTargetingQueue = false;
            HasCachedChanged = false;
        }

        public virtual void CancelTimer()
        {
        }

        public virtual void Activate()
        {
            if (HasRangeCollider)
            {
                RangeCollider.Activate();
            }
            if (HasSpriteRenderer)
            {
                SpriteRenderer.enabled = true;
            }
            enabled = true;
        }

        public virtual void Deactivate()
        {
            if (HasRangeCollider)
            {
                RangeCollider.Deactivate();
            }
            if (HasSpriteRenderer)
            {
                SpriteRenderer.enabled = false;
            }
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
            else if (rangeCircle != null)
            {
                Destroy(rangeCircle.gameObject);
            }
            if (rangeColliderTransform != null)
            {
                RangeCollider rangeCollider = rangeColliderTransform.GetComponent<RangeCollider>();
                RangeCollider = rangeCollider;

                if (Ship.ShipType == ConfigData.ShipTypes.Frigate)
                {
                    RangeCollider.Create(this, Range - 7);
                }
                else
                {
                    RangeCollider.Create(this, Range);
                }
            }
        }

        protected virtual void SetTargetShip(Ship targetShip)
        {
            TargetShip = targetShip;
        }

        private bool _foundTarget;
        private int _index;
        private Ship _potentialTargetShip;
        private ShipDamageStatus _shipDamageStatus;

        /// <summary>Goes through the list of ships in the sorted targeting list and sets the weapon to attack whichever ship is first valid</summary>
        public bool DetermineTargetShip(List<Ship> ships, bool useShipDamageStatus)
        {
            _foundTarget = false;

            for (_index = 0; _index < ships.Count; _index++)
            {
                _potentialTargetShip = ships[_index];
                if (!_foundTarget)
                {
                    if (IsShipValidTarget(_potentialTargetShip))
                    {
                        _shipDamageStatus = Level.State.GetShipDamageStatus(Side, _potentialTargetShip);
                        if (useShipDamageStatus)
                        {
                            if (_shipDamageStatus.TotalDamageSentToShip <= _shipDamageStatus.Health)
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
            }

            if (ships.Count == 0)
            {
                CachedTargetingQueue.Clear();
            }
            return _foundTarget;
        }

        /// <summary>
        /// Checks if the ship is within range
        /// </summary>
        public virtual bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead && IsShipWithinRange(potentialTargetShip);
        }

        private List<Ship> _queue;

        /// <summary> Called every 1/3 Rate of Fire. Makes and sends the sorted targeting list to DetermineTargetShip. Every time this method is called, a target ship should be selected if there is one available </summary>
        public void Targeting()
        {
            TargetShip = null;
            if (Ship.IsUserControlled)
            {
                _queue = MakeSortedTargetingList(false);
                if (!DetermineTargetShip(_queue, true))
                {
                    DetermineTargetShip(_queue, false);
                }
            }
            else
            {
                if ((Ship.Squad.HasCommand || Ship.HasBrain))
                {
                    _queue = MakeSortedTargetingList(false);
                    if (!DetermineTargetShip(_queue, true))
                    {
                        DetermineTargetShip(_queue, false);
                    }
                }
            }
        }

        List<Ship> _enemies;

        /// <summary>
        /// Grabs all ships in the enemy squad within range. If there is no enemy squad, grabs all enemy ships within range
        /// </summary>
        public List<Ship> GetEnemyShipsWithinRange()
        {
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                _enemies = ShipsWithinRange.Where((s) => s.Value.Squad == Ship.Squad.GetCommand().EnemySquad).Select((s) => s.Value).ToList();
                if (_enemies.Count > 0)
                {
                    return _enemies;
                }
                return ShipsWithinRange.Select((s) => s.Value).ToList();
            }
            else
            {
                return ShipsWithinRange.Select((s) => s.Value).ToList();
            }
        }

        private List<Ship> _shipQueue;

        /// <summary>
        /// Gets all the ships that this weapon could potentially target. Either the ships within range or the ships in the enemy squad regardless of range
        /// </summary>
        protected virtual List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            if (disregardRange)
            {
                // GetShips() exposes the squad's authoritative list. Targeting strategies may
                // sort or shuffle their input, so never hand that shared list to the sorter.
                _shipQueue = Ship.Squad.GetCommand().EnemySquad.GetShips().ToList();
            }
            else
            {
                _shipQueue = GetEnemyShipsWithinRange();
            }
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            IsUsingCachedTargetingQueue = false;
            return _shipQueue;
        }

        /// <summary>
        /// Clears the targeting cache and the target ship. Marks the cache as changed
        /// </summary>
        public void ClearTargets()
        {
            TargetShip = null;
            CachedTargetingQueue.Clear();
            HasCachedChanged = true;
        }

        private List<Ship> _sortedQueue;

        /// <summary>Sorts the potential target ships according to the shooting strategy. Uses a cached queue </summary>
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
                    case ConfigData.ShootingStrategyTypes.FirstSeen:
                        return _sortedQueue;
                    case ConfigData.ShootingStrategyTypes.Random:
                        _sortedQueue.Shuffle();
                        break;
                    case ConfigData.ShootingStrategyTypes.Revenge:
                        _sortedQueue.Sort((a, b) => b.LastKilled.CompareTo(a.LastKilled));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostDangerous:
                        _sortedQueue.Sort((a, b) => b.FleetShip.DamageDone.CompareTo(a.FleetShip.DamageDone));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastHealth:
                        _sortedQueue.Sort((a, b) => (a.Health - a.OriginalHealth).CompareTo(b.Health - b.OriginalHealth));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostHealth:
                        _sortedQueue.Sort((a, b) => b.Health.CompareTo(a.Health));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostPowerful:
                        _sortedQueue.Sort((a, b) => b.Firepower.CompareTo(a.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastPowerful:
                        _sortedQueue.Sort((a, b) => a.Firepower.CompareTo(b.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.Closest:
                        _sortedQueue.Sort((a, b) => DistanceTo(a).CompareTo(DistanceTo(b)));
                        break;
                    case ConfigData.ShootingStrategyTypes.Furthest:
                        _sortedQueue.Sort((a, b) => DistanceTo(b).CompareTo(DistanceTo(a)));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostRange:
                        _sortedQueue.Sort((a, b) => b.MaxRange.CompareTo(a.MaxRange));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastRange:
                        _sortedQueue.Sort((a, b) => a.MaxRange.CompareTo(b.MaxRange));
                        break;
                    case ConfigData.ShootingStrategyTypes.Fastest:
                        _sortedQueue.Sort((a, b) => b.Speed.CompareTo(a.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.Slowest:
                        _sortedQueue.Sort((a, b) => a.Speed.CompareTo(b.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostValuable:
                        _sortedQueue.Sort((a, b) => b.Tsv.CompareTo(a.Tsv));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastValuable:
                        _sortedQueue.Sort((a, b) => a.Tsv.CompareTo(b.Tsv));
                        break;
                    default:
                        if ((int) strategy > 15)
                        {
                            ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                            _sortedQueue.Sort((a, b) =>
                            {
                                if (a.ShipTypeLetter == type && b.ShipTypeLetter != type)
                                {
                                    return -1;
                                }
                                else if (b.ShipTypeLetter == type && a.ShipTypeLetter != type)
                                {
                                    return 1;
                                }
                                else
                                {
                                    return 0;
                                }
                            });
                            return _sortedQueue;
                        }
                        else
                        {
                            return _sortedQueue;
                        }
                }
            }
            return _sortedQueue;
        }

        protected virtual void SendProjectile()
        {
            if (HasTargetShip)
            {
                Level.State.GetShipDamageStatus(Side, TargetShip).TotalDamageSentToShip += Power;
            }
            PlaySoundEffect();
        }

        protected void PlaySoundEffect()
        {
            if (HasSoundEffect)
            {
                SoundEffect.Play();
            }
        }

        public void StopSoundEffect()
        {
            if (HasSoundEffect)
            {
                SoundEffect.Stop();
            }
        }

        public bool IsShipWithinRange(Ship ship)
        {
            return ShipsWithinRange.ContainsKey(ship.Id);
        }

        public virtual bool IsPointWithinRange(Vector2 point)
        {
            return DistanceToPoint(point) <= Range;
        }

        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }

        public float DistanceTo(Entity entity)
        {
            return DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()));
        }

        public virtual Vector2 GetPosition()
        {
            return Ship.GetPosition();
        }

        public float AngleToPoint(Vector2 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }

        private Vector2 _direction;

        public float GetDegreesTowardsPoint(Vector2 point)
        {
            _direction = point - GetPosition();
            return Mathf.Repeat(-Mathf.Atan2(_direction.x, _direction.y) * Mathf.Rad2Deg, 360f);
        }

        public bool Equals(Weapon weapon)
        {
            return weapon.Id == Id;
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public virtual void ShowRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(true);
            }
        }

        public virtual void HideRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(false);
            }
        }
    }
}
