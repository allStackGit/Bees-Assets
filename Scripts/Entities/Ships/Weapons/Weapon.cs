

using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
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
        public bool IsUsingCachedTargetingQueue, HasCachedChanged, HasRangeCircle, HasRangeCollider, HasSoundEffect;
        public AudioSource SoundEffect;
        /// <summary>
        /// Ships that this weapon can't fire at because an obstacle is in the way
        /// </summary>
        public Dictionary<Ship, string> __TargetingRejectReasons = new Dictionary<Ship, string>();
        public bool CeaseFire => Ship.Squad.CeaseFire;
        public bool HasTargetShip => TargetShip != null;
        public int Id, Side;
        public Level Level;
        public Stage Stage;
        public RangeCollider RangeCollider;
        /// <summary>
        /// Whether a weapon has a target ship and is not cease fire and therefore *should* fire at a target. It may still not be *able* to fire at a target, if for instance it's a turret and not aimed at the target.
        /// </summary>
        public virtual bool ShouldFire => TargetShip != null && !CeaseFire;

        public string __NotShootingReason;
        public List<Ship> __ShipsWithinRange;
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
        }
        /// <summary>
        /// Sets the weapon up for the level, clears out any old data
        /// </summary>
        public virtual void Setup()
        {
            Level = Ship.Level;
            Id = Level.State.GetId();
            
            Name = $"{Ship.Name}: {Piece.name}";
            ClearData();
            
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
        public virtual void SetupRangeCircleAndCollider()
        {
            Transform rangeCircle = Piece.transform.Find("Range Circle");
            Transform rangeColliderTransform = Piece.transform.Find("Range Collider");
            if (rangeCircle != null && rangeColliderTransform != null)
            {
                RangeCircle = rangeCircle.gameObject;
                RangeCircle.transform.localScale = new Vector3(Range * 2, Range * 2, 0);
                HasRangeCircle = true;

                RangeCollider rangeCollider = rangeColliderTransform.GetComponent<RangeCollider>();
                if (rangeCollider != null )
                {
                    RangeCollider = rangeCollider;
                    RangeCollider.Setup(this, Range);
                }

            }
        }


        // Targeting methods
        protected virtual void SetTargetShip(Ship targetShip)
        {
            //Debug.Log("Setting target ship");
            TargetShip = targetShip;
        }
        /// <summary>Goes through the list of ships in the sorted targeting list and sets the weapon to attack whichever ship is first valid</summary>
        public bool DetermineTargetShip(List<Ship> ships, bool useShipDamageStatus)
        {
            //Debug.Log($"Determining Target ship with {FleetShip.Name}!");
            bool foundTarget = false;

            for (int i = 0; i < ships.Count; i++)
            {
                Ship potentialTargetShip = ships[i];
                //Debug.Log($"{name} is firing at {ship.name} which is priority #{i} in because the Shooting strategy is {Squad.GetShootingStrategy()}.");
                if (!foundTarget && potentialTargetShip != null)
                {
                    if (IsShipValidTarget(potentialTargetShip)) // if the target ship is within range of this weapon and otherwise valid
                    {
                        /*
                        Check to make sure that the damage already sent towards the ship is less than the health of the ship previously
                        calculated.
                         */
                        ShipDamageStatus shipDamageStatus = Level.State.GetShipDamageStatus(Side, potentialTargetShip);
                        if (useShipDamageStatus)
                        {
                            if (shipDamageStatus.TotalDamageSentToShip <= shipDamageStatus.Health)
                            {
                                SetTargetShip(potentialTargetShip);
                                foundTarget = true;
                                return foundTarget;
                            }
                            //else
                            //{
                            //    Debug.Log($"{Ship.Name} cannot fire at {potentialTargetShip.Name} because {shipDamageStatus.TotalDamageSentToShip} >= {shipDamageStatus.Health}");
                            //}
                        }
                        else
                        {
                            SetTargetShip(potentialTargetShip);
                            foundTarget = true;
                            return foundTarget;

                        }

                    }
                    else
                    {
                        //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship {potentialTargetShip.Name} is out of range");
                        __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the potential target ship {potentialTargetShip.Name} is out of range";
                        //ShipsWithinRange.Remove(potentialTargetShip);
                    }
                }
                else
                {
                    if (potentialTargetShip == null)
                    {
                        //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship is null");
                        __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the potential target ship is null";
                        // Empty the cached queue of bad results
                        CachedTargetingQueue.Remove(potentialTargetShip);
                    }

                    else if (foundTarget && TargetShip != null)
                    {
                        __TargetingRejectReasons[potentialTargetShip] = $"{potentialTargetShip.Name} rejected: Already found targetship: {TargetShip.Name}";
                    }
                }
            }

            if (ships.Count == 0)
            {
                //Debug.Log($"{Ship.Name} is not find a target for {Piece.name} because the ship queue is empty");
                __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the ship queue is empty";
                CachedTargetingQueue.Clear();
            }
            return foundTarget;
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
                    List<Ship> queue = MakeSortedTargetingList(false);
                    Ship.__SortedTargetingQueue = queue;
                    if (!DetermineTargetShip(queue, true))
                    {
                        DetermineTargetShip(queue, false);
                    }
                }
                else
                {
                    if ((Ship.Squad.HasCommand || Ship.HasBrain)) // if you've got a command, and you're not retreating
                    {
                        List<Ship> queue = MakeSortedTargetingList(false);
                        Ship.__SortedTargetingQueue = queue;
                        if (!DetermineTargetShip(queue, true))
                        {
                            DetermineTargetShip(queue, false);
                        }
                    }
                    else
                    {
                        if (!Ship.Squad.HasCommand)
                        {
                            //Debug.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it doesn't have a command");
                            __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it doesn't have a command";
                        }
                        //else if (Squad.IsRetreating)
                        //{
                        //    //Debug.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating");
                        //    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating";
                        //}
                    }
                }
            }

        }
        /// <summary>
        /// Grabs all ships in the enemy squad within range. If there is no enemy squad, grabs all enemy ships within range
        /// </summary>
        public List<Ship> GetEnemyShipsWithinRange()
        {
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                List<Ship> enemies = ShipsWithinRange.Where((s) => s.Value.Squad == Ship.Squad.GetCommand().EnemySquad).Select((s) => s.Value).ToList();
                if (enemies.Count > 0)
                {
                    return enemies;
                }
                return ShipsWithinRange.Select((s) => s.Value).ToList();
                //return Ship.Squad.GetCommand().Enemy.GetShips().Where((s) => IsShipWithinRange(s)).ToList();
            }
            else
            {
                return ShipsWithinRange.Select((s) => s.Value).ToList();
            }
        }
        /// <summary>
        /// Gets all the ships that this weapon could potentially target. Either the ships within range or the ships in the enemy squad regardless of range
        /// </summary>
        /// <param name="disregardRange"></param>
        /// <returns></returns>
        protected virtual List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            List<Ship> queue;
            if (disregardRange)
            {
                queue = Ship.Squad.GetCommand().EnemySquad.GetShips();
            }
            else
            {
                queue = GetEnemyShipsWithinRange();
            }
            __ShipsWithinRange = queue.ToList(); // [debug]
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            IsUsingCachedTargetingQueue = false;
            return queue;
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
        /// <summary>Sorts the potential target ships according to the shooting strategy. Uses a cached queue </summary>
        public List<Ship> MakeSortedTargetingList(bool disregardRange)
        {
            
            List<Ship> queue = GetPotentialEnemyTargetShips(disregardRange);
            ConfigData.ShootingStrategyTypes strategy = Ship.ShootingStrategy;
            CachedShootingStrategy = strategy;
            CachedTargetingQueue = queue;
            HasCachedChanged = false;
            if (!IsUsingCachedTargetingQueue)
            {
                //Debug.Log($"Making targeting queue for {Ship.Name}. The squad is using {Squad.GetShootingStrategy()}");
                switch (strategy)
                {
                    case ConfigData.ShootingStrategyTypes.FirstSeen:
                        return queue;
                    case ConfigData.ShootingStrategyTypes.Random:
                        queue.Shuffle();
                        break;
                    case ConfigData.ShootingStrategyTypes.Revenge:
                        queue.Sort((a, b) => b.LastKilled - a.LastKilled);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostDangerous:
                        queue.Sort((a, b) => b.FleetShip.DamageDone - a.FleetShip.DamageDone);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastHealth:
                        queue.Sort((a, b) => a.Health - b.Health);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostHealth:
                        queue.Sort((a, b) => b.Health - a.Health);
                        break;
                    case ConfigData.ShootingStrategyTypes.MostPowerful:
                        queue.Sort((a, b) => (int) (b.Firepower - a.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastPowerful:
                        queue.Sort((a, b) => (int) (a.Firepower - b.Firepower));
                        break;
                    case ConfigData.ShootingStrategyTypes.Closest:
                        queue.Sort((a, b) => (int)(DistanceTo(a) - DistanceTo(b)));
                        break;
                    case ConfigData.ShootingStrategyTypes.Furthest:
                        queue.Sort((a, b) => (int)(DistanceTo(b) - DistanceTo(a)));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostRange:
                        queue.Sort((a, b) => b.MaxRange - a.MaxRange);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastRange:
                        queue.Sort((a, b) => a.MaxRange - b.MaxRange);
                        break;
                    case ConfigData.ShootingStrategyTypes.Fastest:
                        queue.Sort((a, b) => (int) (b.Speed - a.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.Slowest:
                        queue.Sort((a, b) => (int)(a.Speed - b.Speed));
                        break;
                    case ConfigData.ShootingStrategyTypes.MostValuable:
                        queue.Sort((a, b) => b.Tsv - a.Tsv);
                        break;
                    case ConfigData.ShootingStrategyTypes.LeastValuable:
                        queue.Sort((a, b) => a.Tsv - b.Tsv);
                        break;
                    default:
                        if ((int) strategy > 15)
                        {
                            ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                            queue.Sort((a, b) =>
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
                            //if (queue.Count > 0)
                            //{
                            //    Debug.Log($"The first entry in the sorted queue is {queue.First().Name}");
                            //}
                            return queue;
                        }
                        else
                        {
                            return queue;
                        }
                }
            }
            return queue;
        }

        protected virtual void SendProjectile() // [projectile-method] [note]
        {
            //Debug.Log("Sending basic projectile");
            if (HasTargetShip)
            {
                ShipDamageStatus shipDamageStatus = Level.State.GetShipDamageStatus(Side, TargetShip);
                shipDamageStatus.TotalDamageSentToShip += Power;
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
            try
            {
                return DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()));
            }
            catch (Exception e)
            {
                Debug.Log($"Entity: {entity}, entity name: {entity.name}, Collider: {entity.Collider}");
                Debug.Log($"GetPosition: {GetPosition()}");
                Debug.Log($"Closest point: {entity?.Collider?.ClosestPoint(GetPosition())}");
                Debug.Log($"Distance to point: {DistanceToPoint(entity.Collider.ClosestPoint(GetPosition()))}");
                throw e;
            }
        }
        public Vector2 GetPosition()
        {
            try
            {
                return Ship.Level.Map.transform.InverseTransformPoint(Piece.transform.position);
            }
            catch (Exception e)
            {
                Debug.Log($"Ship: {Ship}, Level: {Ship?.Level}, Map: {Ship?.Level?.Map}, Piece: {Piece}");
                throw e;
            }
            
        }
        public float GetRotation()
        {
            return Piece.transform.eulerAngles.z;
        }
        public float GetLocalRotation()
        {
            return Piece.transform.localEulerAngles.z;
        }
        public float AngleToPoint(Vector3 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            float radians = AngleToPoint(point);
            float degrees = radians * Mathf.Rad2Deg;
            //Debug.Log($"Angle towards movement point before adjustment {degrees}");
            if (degrees > 0) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees - 180);

            }
            if (degrees < 0) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees) + 180;
            }
            //Debug.Log($"Angle towards movement point after adjustment {degrees}");
            return degrees;
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
        public bool Equals(Weapon weapon)
        {
            return weapon.Id == Id;
        }
        public override int GetHashCode()
        {
            return Id;
        }
    }
}