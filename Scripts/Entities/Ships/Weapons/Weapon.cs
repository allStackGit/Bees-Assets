

using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Weapon : MonoBehaviour
    {

        public Ship Ship, TargetShip;
        public int Range, Power; 
        public float RateOfFire, ProjectileValue, RotationRate, SpecialFirepower, Firepower;
        public GameObject Piece, RangeCircle;
        public ConfigData.ProjectileTypes ProjectileType;
        public List<Ship> CachedTargetingQueue = new List<Ship>();
        public Dictionary<long, Ship> ShipsWithinRange = new Dictionary<long, Ship>();

        public ConfigData.ShootingStrategyTypes CachedShootingStrategy;
        public string Name;
        public ConfigData.WeaponTypes Type;
        public bool IsUsingCachedTargetingQueue, HasCachedChanged, HasSoundEffect;
        public AudioSource SoundEffect;
        /// <summary>
        /// Ships that this weapon can't fire at because an obstacle is in the way
        /// </summary>
        //public Dictionary<Ship, string> __TargetingRejectReasons = new Dictionary<Ship, string>();
        public bool CeaseFire => Ship.Squad.CeaseFire;
        public bool HasTargetShip => TargetShip != null;
        public int Id, Side;
        public Level Level;
        public Stage Stage;
        public bool HasRangeCircle, HasRangeCollider, HasSpriteRenderer;
        public RangeCollider RangeCollider;
        public SpriteRenderer SpriteRenderer;

        //public string __NotShootingReason;
        //public List<Ship> __ShipsWithinRange;

        /// <summary>
        /// Whether a weapon has a target ship and is not cease fire and therefore *should* fire at a target. It may still not be *able* to fire at a target, if for instance it's a turret and not aimed at the target.
        /// </summary>
        public virtual bool ShouldFire => TargetShip != null && !CeaseFire;

        public virtual void Create(Ship ship, ConfigData.WeaponTypes type, int range, int power, float specialFirePower, float rateOfFire, float projectileValue, GameObject piece,
            ConfigData.ProjectileTypes projectileType)
        {
            Ship = ship;
            Side = Ship.Side;
            Stage = Ship.Stage;
            Range = range;
            Power = power;
            SpecialFirepower = specialFirePower;
            //Power = 10;
            ProjectileValue = projectileValue;
            RateOfFire = rateOfFire;
            //Piece =  Instantiate(piece, Vector2.zero, Quaternion.identity);
            //Piece.transform.localScale = Ship.RelativeSizeScale();
            Piece = piece;
            WeaponsData weaponsData = Piece.GetComponent<WeaponsData>();
            SpriteRenderer = weaponsData.SpriteRenderer;
            if (SpriteRenderer != null && Stage.IsRendering)
            {
                HasSpriteRenderer = true;
            }
            Destroy(weaponsData);
            ProjectileType = projectileType;
            Type = type;

            if (!Stage.IsTraining && Stage.Audio.WeaponSounds.ContainsKey(Type))
            {
                HasSoundEffect = true;
                SoundEffect = Instantiate(Stage.Audio.WeaponSounds[Type][Utilities.RandomInt(Stage.Audio.WeaponSounds[Type].Length)]);
                SoundEffect.transform.parent = Piece.transform;
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
            //Piece.transform.parent = ship.transform;
            //Piece.transform.localPosition = (Vector2)piece.transform.position;

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
            //CancelInvoke();
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
            Transform rangeCircle = Piece.transform.Find("Range Circle");
            Transform rangeColliderTransform = Piece.transform.Find("Range Collider");
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
                RangeCollider.Create(this, Range);

            }
        }



        // Targeting methods
        protected virtual void SetTargetShip(Ship targetShip)
        {
            //Debug.Log("Setting target ship");
            TargetShip = targetShip;
        }

        private bool _foundTarget;
        private int _index;
        private Ship _potentialTargetShip;
        private ShipDamageStatus _shipDamageStatus;
        /// <summary>Goes through the list of ships in the sorted targeting list and sets the weapon to attack whichever ship is first valid</summary>
        public bool DetermineTargetShip(List<Ship> ships, bool useShipDamageStatus)
        {
            //Debug.Log($"Determining Target ship with {FleetShip.Name}!");
            _foundTarget = false;

            for (_index = 0; _index < ships.Count; _index++)
            {
                _potentialTargetShip = ships[_index];
                //Debug.Log($"{name} is firing at {ship.name} which is priority #{i} in because the Shooting strategy is {Squad.GetShootingStrategy()}.");
                if (!_foundTarget)
                {
                    if (IsShipValidTarget(_potentialTargetShip)) // if the target ship is within range of this weapon and otherwise valid
                    {
                        /*
                        Check to make sure that the damage already sent towards the ship is less than the health of the ship previously
                        calculated.
                         */
                        _shipDamageStatus = Level.State.GetShipDamageStatus(Side, _potentialTargetShip);
                        if (useShipDamageStatus)
                        {
                            if (_shipDamageStatus.TotalDamageSentToShip <= _shipDamageStatus.Health)
                            {
                                SetTargetShip(_potentialTargetShip);
                                _foundTarget = true;
                                return _foundTarget;
                            }
                            //else
                            //{
                            //    Debug.Log($"{Ship.Name} cannot fire at {potentialTargetShip.Name} because {shipDamageStatus.TotalDamageSentToShip} >= {shipDamageStatus.Health}");
                            //}
                        }
                        else
                        {
                            SetTargetShip(_potentialTargetShip);
                            _foundTarget = true;
                            return _foundTarget;

                        }

                    }
                    //else
                    //{
                    //    //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship {potentialTargetShip.Name} is out of range");
                    //    //__NotShootingReason = $"{Ship.Name} is not find a target for {Name} because the potential target ship {potentialTargetShip.Name} is out of range";
                    //    //ShipsWithinRange.Remove(potentialTargetShip);
                    //}
                }
                //else
                //{
                //    //if (potentialTargetShip == null)
                //    //{
                //    //    //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship is null");
                //    //    //__NotShootingReason = $"{Ship.Name} is not find a target for {Name} because the potential target ship is null";
                //    //    // Empty the cached queue of bad results
                //    //    CachedTargetingQueue.Remove(potentialTargetShip);
                //    //}

                //    //else  if (_foundTarget && TargetShip != null)
                //    //{
                //    //    __TargetingRejectReasons[_potentialTargetShip] = $"{_potentialTargetShip.Name} rejected: Already found targetship: {TargetShip.Name}";
                //    //}
                //}
            }

            if (ships.Count == 0)
            {
                //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the ship queue is empty");
                //__NotShootingReason = $"{Ship.Name} is not find a target for {Name} because the ship queue is empty";
                CachedTargetingQueue.Clear();
            }
            return _foundTarget;
        }

        /// <summary>
        /// Checks if the ship is within range
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public virtual bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead && IsShipWithinRange(potentialTargetShip);
        }
        private List<Ship> _queue;
        /// <summary> Called every 1/3 Rate of Fire. Makes and sends the sorted targeting list to DetermineTargetShip. 
        /// Every time this method is called, a target ship should be selected if there is one available </summary>
        public void Targeting()
        {
            //Debug.Log($"Targeting! with {Ship.FleetShip.Name}");

            TargetShip = null;
            if (!Level.State.IsPaused && !CeaseFire)
            {
                if (Ship.IsUserControlled) // user controlled fire sequence
                {
                    _queue = MakeSortedTargetingList(false);
                    //Ship.__SortedTargetingQueue = queue;
                    if (!DetermineTargetShip(_queue, true))
                    {
                        DetermineTargetShip(_queue, false);
                    }
                }
                else
                {
                    if ((Ship.Squad.HasCommand || Ship.HasBrain)) // if you've got a command, and you're not retreating
                    {
                        _queue = MakeSortedTargetingList(false);
                        //Ship.__SortedTargetingQueue = queue;
                        if (!DetermineTargetShip(_queue, true))
                        {
                            DetermineTargetShip(_queue, false);
                        }
                    }
                    //else
                    //{
                    //    if (!Ship.Squad.HasCommand)
                    //    {
                    //        //Debug.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it doesn't have a command");
                    //        __NotShootingReason = $"{Ship.Name} is not firing {Name} because it is AI controlled and it doesn't have a command";
                    //    }
                    //    //else if (Squad.IsRetreating)
                    //    //{
                    //    //    //Debug.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating");
                    //    //    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating";
                    //    //}
                    //}
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
                //return Ship.Squad.GetCommand().Enemy.GetShips().Where((s) => IsShipWithinRange(s)).ToList();
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
        /// <param name="disregardRange"></param>
        /// <returns></returns>
        protected virtual List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            if (disregardRange)
            {
                _shipQueue = Ship.Squad.GetCommand().EnemySquad.GetShips();
            }
            else
            {
                _shipQueue = GetEnemyShipsWithinRange();
            }
            //__ShipsWithinRange = queue.ToList(); // [debug]
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            IsUsingCachedTargetingQueue = false;
            return _shipQueue;
            //return queue.Where((s) => s != null && !s.IsDead).ToList();
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
                //Debug.Log($"Making targeting queue for {Ship.Name}. The squad is using {Squad.GetShootingStrategy()}");
                switch (strategy)
                {
                    case ConfigData.ShootingStrategyTypes.FirstSeen:
                        return _sortedQueue;
                    case ConfigData.ShootingStrategyTypes.Random:
                        _sortedQueue.Shuffle();
                        break;
                    case ConfigData.ShootingStrategyTypes.Revenge:
                        _sortedQueue.Sort((a, b) => b.LastKilled - a.LastKilled);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostDangerous:
                        _sortedQueue.Sort((a, b) => b.FleetShip.DamageDone - a.FleetShip.DamageDone);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastHealth:
                        _sortedQueue.Sort((a, b) => a.Health - b.Health);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostHealth:
                        _sortedQueue.Sort((a, b) => b.Health - a.Health);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostPowerful:
                        _sortedQueue.Sort((a, b) => (int) (b.Firepower - a.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastPowerful:
                        _sortedQueue.Sort((a, b) => (int) (a.Firepower - b.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.Closest:
                        _sortedQueue.Sort((a, b) => (int)(DistanceTo(a) - DistanceTo(b)));
                        break;
                    case ConfigData.ShootingStrategyTypes.Furthest:
                        _sortedQueue.Sort((a, b) => (int)(DistanceTo(b) - DistanceTo(a)));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostRange:
                        _sortedQueue.Sort((a, b) => b.MaxRange - a.MaxRange);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastRange:
                        _sortedQueue.Sort((a, b) => a.MaxRange - b.MaxRange);
                        break;
                    case ConfigData.ShootingStrategyTypes.Fastest:
                        _sortedQueue.Sort((a, b) => (int) (b.Speed - a.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.Slowest:
                        _sortedQueue.Sort((a, b) => (int)(a.Speed - b.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostValuable:
                        _sortedQueue.Sort((a, b) => b.Tsv - a.Tsv);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastValuable:
                        _sortedQueue.Sort((a, b) => a.Tsv - b.Tsv);
                        break;
                    default:
                        if ((int) strategy > 15)
                        {
                            ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                            _sortedQueue.Sort((a, b) =>
                            {
                                //Debug.Log($"Strategy: {strategy}, Type: {type}, A ShipTypeLetter: {a.ShipTypeLetter}, B ShipTypeLetter: {b.ShipTypeLetter}");
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
                            //if (_sortedQueue.Count > 0)
                            //{
                            //    Debug.Log($"The first entry in the sorted _sortedQueue is {_sortedQueue.First().Name}");
                            //}
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

        protected virtual void SendProjectile() // [projectile-method] [note]
        {
            //Debug.Log("Sending basic projectile");
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

        // distance and position methods
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
            //try
            //{
            //    return DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()));
            //}
            //catch (Exception e)
            //{
            //    Debug.Log($"Entity: {entity}, entity name: {entity.name}, Collider: {entity.Collider}");
            //    Debug.Log($"GetPosition: {GetPosition()}");
            //    Debug.Log($"Closest point: {entity?.Collider?.ClosestPoint(GetPosition())}");
            //    Debug.Log($"Distance to point: {DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()))}");
            //    throw e;
            //}
        }
        public virtual Vector2 GetPosition()
        {
            return Ship.GetPosition();
        }
        public float AngleToPoint(Vector3 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        private float _degrees;
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            _degrees = AngleToPoint(point) * Mathf.Rad2Deg;
            //Debug.Log($"Angle towards movement point before adjustment {degrees}");
            if (_degrees > 0) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
            {
                _degrees = Mathf.Abs(_degrees - 180);

            }
            if (_degrees < 0) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
            {
                _degrees = Mathf.Abs(_degrees) + 180;
            }
            //Debug.Log($"Angle towards movement point after adjustment {degrees}");
            return _degrees;
        }


        public bool Equals(Weapon weapon)
        {
            return weapon.Id == Id;
        }
        public override int GetHashCode()
        {
            return Id;
        }

        // UI Methods
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