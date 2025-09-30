using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;
using Assets.Scripts.Settings;
using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using UnityEngine.UIElements;
using System.Reflection.Emit;
using Unity.Mathematics;
using System.IO;
using NUnit;
using System.Threading;
using UnityEngine.Pool;
using static UnityEngine.GraphicsBuffer;
using UnityEngine.Rendering;

namespace Assets.Scripts.Entities.Ships
{
    public class Ship : Entity
    {
        public bool ShowDebug;
        public int Health, MaxHealth, OriginalHealth, OriginalTsv, Sight, Clearance, MaxRange, HalfMaxRange;
        public float SizeClass, ProjectileValue, Speed, SpecialFirePower, CurrentSpeed, LongestSide;
        public GameObject ShipExplosion, HealthBar, MiniMapIcon, ShipAnimation, MovementMarker;
        public Vector2 TargetCoordinates, FinalDestination, OffsetFromCenter, PathfindingDestination; // the coordinates of where the ship should go, and it's offset from the center of the squad
        /// <summary>
        /// If a ship can't move when it's given target coordinates, they are held here until it can move
        /// </summary>
        public Vector2 WaitingTargetCoordinates;
        public Squad Squad, MotherSquad;
        public float DefaultAngle, TargetDirection;
        public int LastKilled;
        public FleetShip FleetShip = null;
        /// <summary>
        /// The name of the fleetship and the ship Id
        /// </summary>
        public string Name;
        public ConfigData.ShipTypes ShipType;
        public HiveMindVision HiveMindVision;
        public FogOfWarVision FogOfWarVision;
        public ProximityCollider ProximityCollider;
        public SortingGroup SortingGroup;
        /// <summary>
        /// If a ship has not been spawned into the game yet or it has been killed and returned to the pool then it is dead
        /// </summary>
        public bool IsDead;
        /// <summary>
        /// This has the same side as the user and the user has a controller
        /// </summary>
        public bool IsUserControlled;
        /// <summary>
        /// Is this ship capable of movement?
        /// </summary>
        public bool IsMobile;
        /// <summary>
        /// Settings that are set when the ship is created and do not change
        /// </summary>
        public bool HasBrain, IsHiveMindControlled, IsMinionShip, HasTargetCoordinates, IsMiningShip, IsWarpGate, IsBeehive, HasTargetDirection, HasUserFogOfWarVision, HasProximityCollider, HasShipAnimation, HasRocketFlares,
            HasLeftRocketFlares, HasCenterRocketFlares, HasRightRocketFlares, HasOnlySideRocketFlares, HasMovementMarker, HasWaitingTargetCoordinates, HasRemainsShip, FireAtFrontOfShip, IsBomber;
        /// <summary>
        /// Whether the ship is spawned by the game and has a negative id or is part of the tracked fleets
        /// </summary>
        public bool IsSpawnedShip;
        /// <summary>
        /// Settings that change over the lifetime of the ship
        /// </summary>
        public bool HasEnteredMap, AreRocketFlaresOutOfSync, InCombat, IsFollowingPath, CannotChangeMovementOrders;
        public List<Weapon> Weapons;
        /// <summary>
        /// All the prefabs on the ship that need to be recolored to the squad color
        /// </summary>
        public List<GameObject> ColoredPrefabs;
        /// <summary>
        /// All of the original colored prefabs belonging to the ship before alteration
        /// </summary>
        public List<GameObject> OriginalColoredPrefabs;
        /// <summary>
        /// The original uncolored sprites of the ship's prefabs
        /// </summary>
        public List<Sprite> OriginalSprites;
        public List<GameObject> WeaponPrefabs, LeftRocketFlares, CenterRocketFlares, RightRocketFlares;
        public Brain Brain = null;
        public Queue<Vector2> DestinationQueue = new Queue<Vector2>();
        public List<CollisionAsteroid> NearbyAsteroids = new List<CollisionAsteroid>();
        public List<Turret> Turrets = new List<Turret>();
        /// <summary>
        /// Used to detect other ships near this ship if this ship doesn't have a ranged weapon. Used on Strikers, Fire Barges, and Yellow Jackets to detect when they're near targets
        /// </summary>
        //public ShipProximityCollider ProximityCollider;
        /// <summary>
        /// Controls the animation and recoloring of sprites if the ship has an animation
        /// </summary>
        public ShipAnimationController ShipAnimationController;
        public float RotationSpeed;
        /// <summary>
        /// The ship that this ship is following after in order to target it. The primary enemy ship. This is NOT necessarily the ship that this ship is firing at. The weapon(s) have that information
        /// </summary>
        public Ship TargetEnemyShipToFollow;
        /// <summary>
        /// The ship that killed this ship
        /// </summary>
        public Ship Killer;
        public FleetShip KillerFleetShip;
        public SavedSquad KillerSavedSquad;
        /// <summary>
        /// All the projectiles that came from this ship and are not yet dead
        /// </summary>
        public HashSet<Projectile> ProjectilesInFlight = new HashSet<Projectile>();
        /// <summary>
        /// The remains of this ship, whether animated or not. Controls the placing and removing of the remains
        /// </summary>
        public ShipRemains ShipRemains;
        /// <summary>
        /// All the weapon that have this ship within range
        /// </summary>
        public HashSet<Weapon> WeaponsThatHaveUsWithinRange = new HashSet<Weapon>();
        /// <summary>
        /// An enum (int) representation of the ship type letter
        /// </summary>
        public ConfigData.ShipTypeLetters ShipTypeLetter;
        /// <summary>
        /// The chosen shooting strategy of this ship which should in turn match the squad
        /// </summary>
        public ConfigData.ShootingStrategyTypes ShootingStrategy;
        /// <summary>
        /// The sound effect of the ship exploding, if there is one
        /// </summary>
        public AudioSource ShipExplosionSoundEffect;
        public bool HasShipExplosionSoundEffect;
        public float Firepower;
        /// <summary>
        /// The sum of the ship's turrets' power over their rate of fire. Used only for debugging purposes.
        /// </summary>
        public float DamagePerSecond;
        /// <summary>
        /// The value of the ship's TSV + the value of all minerals mined
        /// </summary>
        public int Tsv;
        public bool HasWeapons, HasTurrets;
        public bool IsCarrierShip;
        public bool IsMoving;
        /// <summary>
        /// Whether or not the ship has the Cease Fire command from the squad or from being healed
        /// </summary>
        public bool IsCeaseFire;
        /// <summary>
        /// Whether or not a ship can move beyond the map bounds. Only allowed temporarily for campaign levels
        /// </summary>
        public bool CanOverrideBounds;
        /// <summary>
        /// A set of all the ships that this ship has hit
        /// </summary>
        public HashSet<Ship> ShipsHit = new HashSet<Ship>();

        public volatile bool PathfindingThreadComplete, IsPathfinding;
        public volatile Pathfinder.Path PathfindingValue;
        public volatile int PathfindingThread;
        public volatile Pathfinder.Grid DebugGrid;
        public volatile Pathfinder.MapNode[][] DebugNodes;
        public volatile Pathfinder.MapNode DebugEndNode;
        public volatile Pathfinder.MapNode DebugOriginalEndNode;
        public volatile Pathfinder.MapNode DebugStartNode;
        public volatile Pathfinder.MapNode DebugOriginalStartNode;
        public volatile HashSet<Pathfinder.MapNode> DebugWalkablePointNodes = new HashSet<Pathfinder.MapNode>();
        public volatile bool PrintDebugImage;




        /// <summary>
        /// Whether or not the ship has target coordinates. If it does, it hasn't reached the destination
        /// </summary>
        public bool HasReachedDestination => !HasTargetCoordinates;
        /// <summary>
        /// A list of all the ships that are within range of this ship's weapon(s). Not very performant, use with caution
        /// </summary>
        public List<Ship> ShipsWithinRange => HasWeapons ? Weapons.Select((w) => w.ShipsWithinRange).Aggregate(new HashSet<Ship>(), (list, current) => {
            list.UnionWith(current.Values.ToHashSet());
            return list;
        }).ToList() : new List<Ship>();
        /// <summary>
        /// Means the a ship has a command, that command has live enemies, and this ship is following after one of those enemies. This is seperate from the ship(s) that this ship's weapon(s) are targeting
        /// </summary>
        public bool HasTargetEnemyShipToFollow => TargetEnemyShipToFollow != null  && !TargetEnemyShipToFollow.IsDead;


        private bool _combatTimer, _isInBounds;
        private Transform _healthBarFiller;
        private SpriteRenderer _healthBarFillerSprite;
        private Vector2 _size;
        private float _tempAngle, _tempDistance;


        // Test variables
        public string __Strategy, __Squad, __SavedSquad, __SquadStatus, __CommandStatus, __LastStopReason, __EnemySquad, __TargetEnemyShipToFollow, __SquadColor, __SquadShootingStrategy;
        public Vector2 __CommandDestination, __Velocity, __TargetCoordinates;
        public float __Firepower, __DamagePerSecond, __CurrentSpeed, __DegreesToTargetCoordinates, __DistanceToTargetCoordinates, __TurningRadius, __Width, __Height, __SquadWidth, 
            __SquadHeight;
        public long __Tsv, __CommandTsv;
        public bool __HasReachedDestination, __SquadHasReachedDestination, __IsInBounds;
        public List<Ship> __WeaponTargetShips, __SquadShips, __NearbyShips, __ShipsWarpingHere, __ShipsOnTopOf, __SortedTargetingQueue;
        public List<string> __ShipsWithinRangeOfWeapons, __PastCommands, __BannedStrats, __DamageStatuses, __CommandTargetingQueue, __NearbyAsteroids, __HivemindShips, __RejectReasons;
        public int __Clearance, __MineralsMined;
        //public List<Vector2> __PastLocations;


        // Neural network
        public int Direction;
        public bool ShouldDetonate;
        public ConfigData.ShootingStrategyTypes RLShootingStrategy;
        public float RLSide;
        public float RLHealth;
        public float RLShipType;


        private int _tempIndex;
        private Obstacle _tempObstacle;
        private Vector2 _tempVelocity;
        private GameObject _tempCollidingThing;

        //private ShipDamageStatus _tempShipDamageStatus;

        protected virtual void UpdateDebugProperties()
        {
            __Strategy = $"{Squad?.GetCommand()?.CommandType} - {Squad?.GetCommand()?.OutcomeId}";
            __EnemySquad =  Squad.HasEnemy ? Squad.GetCommand().EnemySquad.Name : "-";
            __ShipsWithinRangeOfWeapons = ShipsWithinRange.Select((ship) => ship.Name).ToList();
            __Squad = Squad.Name;
            __SavedSquad = Squad.SavedSquad.Name;
            __SquadStatus = Squad.Status;
            //__CommandStatus = Squad.HasCommand ? Squad.Comd.Status : "-";
            __CommandDestination = Squad.HasCommand ? Squad.GetCommand().GetDestination() : Vector2.zero;
            __TargetCoordinates = TargetCoordinates;
            if (IsMobile)
            {
                __Velocity = Body.linearVelocity;
            }
            __Firepower = Firepower;
            __Tsv = Tsv;
            __DamagePerSecond = DamagePerSecond;
            __CommandTsv = Squad.HasCommand ? Squad.GetCommand().Tsv : 0;
            __PastCommands = Squad.PastCommands.Select((c) => c.IsFinalized ? $"#{c.OutcomeId} - {c.CommandType} ({c.Tsv}) against {c.Enemy} ended" +
            $" due to \"{c.FinalizationCause}\" and took {c.Age} ticks" : $"#{c.OutcomeId} - {c.CommandType} (Unfinalized)").ToList();

            __HasReachedDestination = HasReachedDestination;
            __SquadHasReachedDestination = Squad.HasReachedDestination;
            __SquadShips = Squad.GetShips();
            __BannedStrats = Squad.BannedStrats.Select((b) => b.ToString()).ToList();
            __DamageStatuses = Level.State.ShipDamageStatuses[Side - 1].Select((ds) => $"{ds.TotalDamageSentToShip} damage sent to {ds.Ship.Name} against {ds.Health} health. Current health: {ds.Ship.Health}").ToList();
            __TargetEnemyShipToFollow = HasTargetEnemyShipToFollow ? $"Following {TargetEnemyShipToFollow.Name} at {TargetEnemyShipToFollow.GetPosition()}" : "None";
            __CommandTargetingQueue = Squad.HasCommand && Squad.GetCommand().HasEnemy ? Squad.GetCommand().TargetingQueue.Select((ship) =>  ship.Name).ToList() : new List<string>();
            __CurrentSpeed = CurrentSpeed;
            __NearbyAsteroids = NearbyAsteroids.Select((a) => a.Name).ToList();
            __DegreesToTargetCoordinates = GetDegreesTowardsPoint(TargetCoordinates);
            __DistanceToTargetCoordinates = DistanceToPoint(TargetCoordinates);
            __TurningRadius = ConfigData.ShipTurningRadius;
            __NearbyShips = HasProximityCollider ? ProximityCollider.NearbyEnemyShips.ToList() : new List<Ship>();
            __HivemindShips = Level.State.GetShipsVisibleToHiveMind(Side).Select(s => s.ToString()).ToList();
            __Clearance = GetClearance();
            __IsInBounds = IsInBounds();
            __SquadColor = ColorUtility.ToHtmlStringRGB(Squad.Color);
            __Width = GetWidth();
            __Height = GetHeight();
            __SquadShootingStrategy = Squad.GetShootingStrategy().ToString();
            __MineralsMined = FleetShip.MineralsMinedThisLevel;
            //__SquadWidth = Squad.GetWidth();
            //__SquadHeight = Squad.GetHeight();

            //__BlockedShips = Weapons.Aggregate(new HashSet<Ship>(), (sum, weapon) => {
            //    sum.UnionWith(weapon.BlockedShips.Where((ship) => ship != null && !ship.IsDead));
            //    return sum;
            //}).ToList();

            //__RejectReasons = Weapons.Aggregate(new HashSet<string>(), (sum, weapon) => {
            //    sum.UnionWith(weapon.__TargetingRejectReasons.Values);
            //    return sum;
            //}).ToList();

            if (ShipType == ConfigData.ShipTypes.WarpGate)
            {
                __ShipsWarpingHere = ((WarpGate)this).ShipsWarpingHere.Select((s) => Level.State.GetShipById(s)).ToList();
            }

            //if (HasEnteredMap && Vector2.Distance(GetPosition(), Level.ForceBounds(GetPosition())) > 20)
            //{
            //    Debug.Log($"{Name} is well out of bounds!");
            //    throw new Exception();
            //}


            //__PastLocations = PastLocations.ToList();
            //AverageReward = AverageRewardSum / Actions;
            //AverageRandomReward = AverageRandomRewardSum / RandomActions;
            //AverageLearnedReward = AverageLearnedRewardSum / LearnedActions;
            //for (int i = 0; i < AverageDirectionReward.Length; i++)
            //{
            //    AverageDirectionReward[i] = AverageDirectionSum[i] / DirectionActionCount[i];
            //}
        }

        // setup methods
        /// <summary>
        /// Sets up a ship for the Ship pool so it is ready to go regardless of which level it's in and other identifying factors
        /// </summary>
        public override void Create(Stage stage)
        {
            base.Create(stage);
            ShipStatBlock shipStats = ConfigData.GetShipInfo(ShipType);
            Sight = shipStats.Sight;
            Speed = shipStats.Speed;
            OriginalHealth = shipStats.Health;
            MaxHealth = OriginalHealth;
            Clearance = Stage.ShipClearances.GetValueOrDefault(ShipType);
            _healthBarFiller = HealthBar.transform.GetChild(0);
            _healthBarFillerSprite = HealthBar.transform.GetChild(0).GetComponent<SpriteRenderer>();
            IsUserControlled = Side == ConfigData.Configuration.UserSide && Stage.DoesUserHaveController;
            RotationSpeed = Speed * ConfigData.Configuration.RotationMultiplier;
            IsMobile = Speed > 0;

            if (!IsUserControlled)
            {
                IsHiveMindControlled = true;
            }
            //Transform brain = transform.Find("Brain");
            //if (brain != null && Level.Stage.ActivateBrains)
            //{
            //    //Debug.Log($"Found a brain for {Name}, {brain}");
            //    Brain = brain.GetComponent<Brain>();
            //    Brain.Setup(this);
            //    HasBrain = true;

            //    RLSide = Side / 2;
            //    RLHealth = Health / MaxHealth;
            //    RLShipType = (float)Utilities.ShipTypeLetterToInt[ShipTypeLetter] / Utilities.ShipTypesAndTypeLetters.Count;
            //}

            //if (ProximityCollider != null)
            //{
            //    ProximityCollider.Create(this, Sight);
            //    HasProximityCollider = true;
            //}

            if (!Stage.IsTraining)
            {
                if (Side == ConfigData.Configuration.HumanSide)
                {
                    if (LeftRocketFlares.Count > 0)
                    {
                        HasLeftRocketFlares = true;
                    }
                    if (CenterRocketFlares.Count > 0)
                    {
                        HasCenterRocketFlares = true;
                    }
                    if (RightRocketFlares.Count > 0)
                    {
                        HasRightRocketFlares = true;
                    }

                    if (HasLeftRocketFlares || HasCenterRocketFlares || HasRightRocketFlares)
                    {
                        HasRocketFlares = true;
                    }
                    else
                    {
                        HasRocketFlares = false;
                    }
                }
                else
                {
                    HasRocketFlares = false;
                }

                if (ShipAnimation != null)
                {
                    HasShipAnimation = true;

                    if (ShipAnimationController != null)
                    {
                        ShipAnimationController.Setup();
                    }
                }

                if (HasRemainsShip) // [testing] all ships should have multiple shattered ships eventually
                {
                    ShipRemains = Instantiate(Stage.Prefabs.ConvertShipTypeToRemainsPrefab[ShipType], Vector2.zero, Quaternion.identity).AddComponent<ShipRemains>();
                    ShipRemains.Create(this);
                }

                if (ShipType != ConfigData.ShipTypes.FireBarge)
                {
                    ShipExplosion = Instantiate(Stage.Prefabs.ConvertShipTypeToExplosionPrefab[ShipType], Vector2.zero, Quaternion.identity);
                    ShipExplosion.SetActive(false);
                    //ShipExplosion.GetComponent<ShipExplosionAnimation>().Create(this);

                }


                if (Stage.ActivateAudio)
                {
                    if (ShipType != ConfigData.ShipTypes.FireBarge)
                    {
                        ShipExplosionSoundEffect = ShipExplosion.GetComponent<AudioSource>();
                        if (ShipExplosionSoundEffect != null)
                        {
                            HasShipExplosionSoundEffect = true;
                        }

                    }
                    
                }
            }
            else
            {
                Destroy(SortingGroup);
                Destroy(MiniMapIcon);
                LeftRocketFlares.ForEach((flare) =>
                {
                    Destroy(flare);
                });
                LeftRocketFlares.Clear();
                CenterRocketFlares.ForEach((flare) =>
                {
                    Destroy(flare);
                });
                CenterRocketFlares.Clear();
                RightRocketFlares.ForEach((flare) =>
                {
                    Destroy(flare);
                });
                RightRocketFlares.Clear();
                HasRocketFlares = false;
                HasRemainsShip = false;
            }
            if (!Stage.IsRendering)
            {
                Destroy(HealthBar);
            }

            if (ShipType == ConfigData.ShipTypes.Striker || ShipType == ConfigData.ShipTypes.Barge)
            {
                SpecialFirePower = shipStats.Powers[0] / 3;
            }
            else if (ShipType == ConfigData.ShipTypes.FireBarge)
            {
                SpecialFirePower = shipStats.Powers[0] * shipStats.ProjectileValues[0];
            }
            else if (ShipType == ConfigData.ShipTypes.YellowJacket)
            {
                SpecialFirePower = shipStats.Powers[0] / 5;
            }
            else if (ShipType == ConfigData.ShipTypes.CarpenterBee || ShipType == ConfigData.ShipTypes.Factory)
            {
                IsMiningShip = true;
            }
            else if (ShipType == ConfigData.ShipTypes.WarpGate)
            {
                IsWarpGate = true;
            }
            else if (ShipType == ConfigData.ShipTypes.Beehive)
            {
                IsBeehive = true;
            }

            Weapon weapon;
            for (int i = 0; i < shipStats.ProjectileValues.Count; i++)
            {
                weapon = null;
                if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.Turret)
                {
                    weapon = gameObject.AddComponent<Turret>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.LightCannon)
                {
                    weapon = gameObject.AddComponent<Turret>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.RocketTurret)
                {
                    weapon = gameObject.AddComponent<Turret>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.Eye)
                {
                    weapon = gameObject.AddComponent<Eye>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.QueenEye)
                {
                    weapon = gameObject.AddComponent<QueenEye>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.Bomb)
                {
                    weapon = gameObject.AddComponent<Bomb>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.SplitShot)
                {
                    weapon = gameObject.AddComponent<LaserBuilder>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.DualCannon)
                {
                    weapon = gameObject.AddComponent<DualCannon>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.BeamCannon)
                {
                    weapon = gameObject.AddComponent<BeamCannon>();
                }
                else if (shipStats.WeaponTypes[i] == ConfigData.WeaponTypes.FullShipTurret)
                {
                    weapon = gameObject.AddComponent<FullShipTurret>();
                }
                else
                {
                    Debug.LogError($"{Name}'s weapon #{i} doesn't have a proper weapon type: {shipStats.WeaponTypes[i]}");
                }


                if (weapon is Turret)
                {
                    //Debug.Log($"it's a turret!");
                    if (weapon is Eye)
                    {
                        //if (ShipType == ConfigData.ShipTypes.Hornet)
                        //{
                        //    shipStats.Ranges[i] = 80;
                        //}

                        ((Eye)weapon).Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else if (weapon is LaserBuilder)
                    {
                        ((LaserBuilder)weapon).Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else if (weapon is FullShipTurret)
                    {
                        ((FullShipTurret)weapon).Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else
                    {
                        ((Turret)weapon).Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }

                }
                else
                {
                    //Debug.Log($"{weapon.GetType()} -- {typeof(Turret)}");
                    weapon.Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], SpecialFirePower, shipStats.RatesOfFire[i],
                    shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i]);
                }

                Weapons.Add(weapon);
            }

            Turrets = Weapons.Where((w) => w is Turret).ToList().ConvertAll((w) => (Turret)w);
            HalfMaxRange = MaxRange / 2;
            HasWeapons = Weapons.Count > 0;
            HasTurrets = Turrets.Count > 0;
            MaxRange = HasWeapons ? Weapons.Max((w) => w.Range) : 0;
            Firepower = HasWeapons ? Weapons.Sum(w => w.Firepower) : SpecialFirePower;
            DamagePerSecond = Turrets.Sum(t => t.DamagePerSecond);
            _maxRateOfFire = HasWeapons ? Weapons.Max((w) => w.RateOfFire) : 2;
            _repeatRate = Mathf.Clamp(5f, _maxRateOfFire + 1, _maxRateOfFire + 2);

            _size = ConfigData.ShipSizes[ShipType] / ConfigData.PixelsPerUnit;

            OriginalTsv = Utilities.GetMaxTsv(this.ShipType); // Must be calculated after health, firepower, and speed are set
            SetCurrentSpeed(Speed);

            if (IsUserControlled)
            {
                if (IsMobile)
                {
                    MovementMarker = Instantiate(Stage.Prefabs.MovementMarkerPrefab, Vector2.zero, Quaternion.identity);
                    MovementMarker.SetActive(false);
                    HasMovementMarker = true;
                }

                HasUserFogOfWarVision = true;
                FogOfWarVision.Create(this);
                Destroy(HiveMindVision.gameObject);

                OriginalColoredPrefabs.Insert(0, gameObject);
            }
            else
            {
                HiveMindVision.Create(this); // Has to happen after MaxRange is calculated
                Destroy(FogOfWarVision.gameObject);
            }
            if (HasProximityCollider)
            {
                ProximityCollider.Create(this);
            }

            if (GetWidth() > GetHeight())
            {
                LongestSide = GetWidth();
            }
            else
            {
                LongestSide = GetHeight();
            }

            Deactivate();
        }
        /// <summary>
        /// Sets up a hip for a level with a squad, fleetship, and other identifying factores
        /// </summary>
        /// <param name="level"></param>
        /// <param name="id"></param>
        /// <param name="fleetShip"></param>
        /// <param name="squad"></param>
        /// <param name="offsetFromCenter"></param>
        public virtual void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter) 
        {
            //Debug.Log($"Setting up ship IsCarrierShip: {IsCarrierShip}");
            Squad = squad;
            Level = level;
            Id = Level.State.GetId();
            FleetShip = fleetShip;
            OffsetFromCenter = offsetFromCenter;
            Health = OriginalHealth;
            Name = $"{FleetShip.Type} #{FleetShip.Id}";
            gameObject.name = Name;
            ClearData();

            if (IsHiveMindControlled)
            {
                Level.State.HivemindShips[Side - 1].Add(Id, new HashSet<Ship>());
                //Debug.Log($"Added {Name} to hivemind ships");
            }



            if (FleetShip.Id < 0)
            {
                IsSpawnedShip = true;
            }

            if (!Level.Stage.IsTraining)
            {
                if (squad.HasCustomColor)
                {
                    Utilities.SetUIColor(MiniMapIcon, squad.Color);
                    if (HasShipAnimation)
                    {
                        ShipAnimationController.RecolorAnimationSprites();
                    }
                }
                else if (Side == ConfigData.Configuration.HumanSide)
                {
                    Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("human"));
                }
                else if (Side == ConfigData.Configuration.BeeSide)
                {
                    Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("bee"));
                }
            }

            

            
            //squad.AddShip(this);
            Level.State.AddShip(this);

            if (IsWarpGate)
            {
                Level.State.HasWarpGates = true;
            }
            else if (IsBeehive)
            {
                Level.State.HasBeehives = true;
            }

            if (IsUserControlled)
            {
                if (IsMobile)
                {
                    MovementMarker.transform.SetParent(Level.Map.Transform);
                    MovementMarker.name = $"{Name}'s Movement Marker";

                    if (Squad.HasCustomColor)
                    {
                        MovementMarker.GetComponent<SpriteRenderer>().color = Squad.Color;
                    }
                    else
                    {
                        if (Side == ConfigData.Configuration.HumanSide)
                        {
                            MovementMarker.GetComponent<SpriteRenderer>().color = ConfigData.GetUIColor("human");
                        }
                        else
                        {
                            MovementMarker.GetComponent<SpriteRenderer>().color = ConfigData.GetUIColor("bee");
                        }
                    }
                }

            }

            Weapons.ForEach((weapon) =>
            {
                weapon.Setup();
            });

            if (HasRemainsShip)
            {
                ShipRemains.Setup();
            }

            if ((ConfigData.Configuration.UserSide == Side || !Level.HasPlayer) && (ShipType == ConfigData.ShipTypes.Factory || ShipType == ConfigData.ShipTypes.CarpenterBee))
            {
                Level.State.MiningShips.Add(this);
            }
            UpdateHealthBar();
            Activate();
            //Debug.Log($"Ship {Name} has been setup and activated");
        }
        public virtual void ClearData()
        {
            Rotation = OriginalRotation;
            Tsv = OriginalTsv;
            Transform.eulerAngles = new Vector3 (0, 0, OriginalRotation); // Is this needed?
            PathfindingDestination = Vector2.zero;
            SetTargetCoordinates(Vector2.zero);
            HasTargetCoordinates = false;
            HasTargetDirection = false;
            FinalDestination = Vector2.zero;
            LastKilled = 0;
            CannotChangeMovementOrders = false;
            IsFollowingPath = false;
            InCombat = false;
            IsDead = false;
            AreRocketFlaresOutOfSync = false;
            HasEnteredMap = false;
            DestinationQueue.Clear();
            NearbyAsteroids.Clear();
            TargetEnemyShipToFollow = null;
            Killer = null;
            KillerFleetShip = null;
            KillerSavedSquad = null;
            ProjectilesInFlight.Clear();
            WeaponsThatHaveUsWithinRange.Clear();
            SetToDefaultAngle();
            CanOverrideBounds = false;
            ShipsHit.Clear();

            //if (HasProximityCollider)
            //{
            //    ProximityCollider.Setup();
            //}
        }
        protected void FixedUpdate()
        {
            // Debug angles from ships
            //if (Squad.IsSelected)
            //{
            //    Debug.Log(GetRotatedAngleToPoint(Level.InputManager.GetMousePosition()));
            //}
            if (Level.HasObstacles && PathfindingThreadComplete)
            {
                MergePathfindingPaths();
                PathfindingThreadComplete = false;
            }
            Move();
            if (Stage.DebugLogger.IsDebugging || ShowDebug) // [alert] [debug] remove this for release
            {
                UpdateDebugProperties();
            }
        }
        private Color[] _colors;
        private Sprite _prefabSprite, _loadedSprite, _shipIcon, _recolored;
        private Vector2Int _setColorSize = Vector2Int.zero;
        private bool _hasLoadedSprite;
        private int[] _changablePixels;
        //private string _status;
        public virtual void SetColor()
        {
            // set the color
            if (Squad.HasCustomColor)
            {
                //Debug.Log("Setting sprite for ship");
                //float start = Time.realtimeSinceStartup;
                //string status = "Loading";
                OriginalSprites.Clear();
                ColoredPrefabs = OriginalColoredPrefabs.ToList();
                _colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                _tempIndex = 0;

                ColoredPrefabs.ForEach((prefab) =>
                {
                    _prefabSprite = prefab.GetComponent<SpriteRenderer>().sprite;
                    OriginalSprites.Add(_prefabSprite);
                    _setColorSize = new Vector2Int(_prefabSprite.texture.width, _prefabSprite.texture.height);
                    _hasLoadedSprite = false;
                    if (FleetShip.HasCachedSprite)
                    {
                        //_status = "loading";

                        _loadedSprite = FleetShip.LoadCachedSprite(_tempIndex, "ship", _setColorSize, Squad.SavedSquad.Color);
                        if (_loadedSprite != null)
                        {
                            prefab.GetComponent<SpriteRenderer>().sprite = _loadedSprite;
                            _hasLoadedSprite = true;
                        }
                    }
                    if (!_hasLoadedSprite)
                    {
                        //_status = "Drawing";
                        _shipIcon = _prefabSprite;
                        _changablePixels = Utilities.GetChangablePixelsForImage(_colors, _shipIcon);
                        _recolored = Utilities.SetImageColor(Squad.Color, _shipIcon, _changablePixels);
                        prefab.GetComponent<SpriteRenderer>().sprite = _recolored;
                    }
                    _tempIndex++;
                });


                //Debug.Log($"{_status} sprites for {FleetShip.Name} took {(Time.realtimeSinceStartup - start)*1000}ms");
            }
            else if (OriginalSprites.Count > 0)
            {
                _tempIndex = 0;

                ColoredPrefabs.ForEach((prefab) =>
                {
                    prefab.GetComponent<SpriteRenderer>().sprite = OriginalSprites[_tempIndex];
                    _tempIndex++;
                });
            }
        }
        public void SetSquadName() // [debug]
        {
            // Set the name of the ships with the Squad name
            Name = $"{Squad.Name}: {Name}";
            gameObject.name = Name;
        }


        // movement methods
        Vector2Int _convertedStart, _convertedDestination;
        Vector2 _startPosition;
        private Collider2D _obstacleCollider;
        public void MoveToPoint(Vector2 destination, bool foundObstacle = false)
        {
            Debug.Log($"{this} is moving to {destination}");
            if (!CannotChangeMovementOrders)
            {
                destination = CanOverrideBounds ? destination : Level.ForceBounds(destination);

                if (Level.HasObstacles && IsInBounds())
                {
                    _startPosition = Level.ForceBounds(GetPosition());
                    DestinationQueue.Clear();
                   

                    if (foundObstacle)
                    {
                        //Debug.Log($"Found obstacle in the way of {Name}");
                        _convertedStart = Level.Pathfinder.ConvertToMapCoordinates(_startPosition);
                        _convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                        //StopMoving("Got a new destination");
                        ClearPreviousDesintation();
                        if (!IsPathfinding)
                        {
                            Level.Pathfinder.FindPath(this, _convertedStart.x, _convertedStart.y, _convertedDestination.x, _convertedDestination.y, GetClearance());
                            PathfindingDestination = destination;
                        }
                        else
                        {
                            //Debug.Log($"{Name} is already pathfinding on {PathfindingThread} so it can't pathfind right now");
                        }
                        return;
                    }
                    else
                    {
                        if (Vector2.Distance(destination, TargetCoordinates) < ConfigData.CloseEnoughCoordinateVariance)
                        {
                            //Debug.Log($"The difference between our new destination and old destination for {Name} is {Vector2.Distance(destination, TargetCoordinates)} so there's no need to generate new pathfinding about it");
                            return;
                        }
                        _obstacleCollider = GetObstacleInPath(destination);
                        if (_obstacleCollider != null)
                        {
                            _tempObstacle = _obstacleCollider.GetComponent<Obstacle>();
                            Debug.Log($"{_tempObstacle.Name} is in the way of {Name}");
                            if (_tempObstacle.ObstacleType != ConfigData.ObstacleTypes.CollisionAsteroid)
                            {
                                //CollisionAsteroid asteroid = (CollisionAsteroid)obstacle;
                                //if (!NearbyAsteroids.Contains(asteroid)){
                                //    NearbyAsteroids.Add(asteroid);
                                //}
                                _convertedStart = Level.Pathfinder.ConvertToMapCoordinates(_startPosition);
                                _convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                                //StopMoving("Got a new destination");
                                ClearPreviousDesintation();

                                if (!IsPathfinding)
                                {
                                    Level.Pathfinder.FindPath(this, _convertedStart.x, _convertedStart.y, _convertedDestination.x, _convertedDestination.y, GetClearance());
                                    PathfindingDestination = destination;
                                }
                                else
                                {
                                    //Debug.Log($"{Name} is already pathfinding on {PathfindingThread} so it can't pathfind right now");
                                }
                                return;
                            }

                            
                        }
                        else
                        {
                            Debug.Log($"Direct path for {Name} to {destination}");
                        }

                    }

                    //convertedStart = Level.Pathfinder.ConvertToMapCoordinates(startPosition);
                    //convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                    //Level.Pathfinder.FindPath(this, convertedStart.x, convertedStart.y, convertedDestination.x, convertedDestination.y, GetClearance());

                }
                else if (!IsInBounds())
                {
                    Debug.Log($"{Name} cannot pathfind because it's not in bounds");
                }
                //else
                //{
                //    Debug.Log($"No obstacles in the way for {Name}");

                //}
                //StopMoving("Got a new destination");
                Debug.Log($"Got a new destination for {Name}, moving to {destination}");
                ClearPreviousDesintation();
                IsFollowingPath = false;
                SetTargetCoordinates(destination);
                FinalDestination = TargetCoordinates;
                HasTargetCoordinates = true;

                MoveMovementMarker();
            }
            else
            {
                WaitingTargetCoordinates = destination;
                HasWaitingTargetCoordinates = true;
            }
            
        }
        public void MoveToDirectionOfPoint(Vector2 directionPoint)
        {
            directionPoint = Level.ForceBounds(directionPoint);
            MoveInDirection(GetDegreesTowardsPoint(directionPoint));
        }
        public void MoveInDirection(float direction)
        {
            if (!CannotChangeMovementOrders)
            {
                StopMoving("Got a new destination");

                IsFollowingPath = false;
                HasTargetCoordinates = false;
                HasTargetDirection = true;
                TargetDirection = direction;
            }

        }
        private ScaledTimer _asteroidDoubleCheckTimer = new ScaledTimer();
        private bool _isDoubleCheckingForAsteroids = false;
        /// <summary>
        /// This is triggered by the asteroid when the ship gets within its proximity collider
        /// </summary>
        /// <param name="asteroid"></param>
        public void FoundNearbyAsteroid(CollisionAsteroid asteroid)
        {
            if (!NearbyAsteroids.Contains(asteroid))
            {
                NearbyAsteroids.Add(asteroid);
            }
            //Debug.Log($"There's an asteroid {asteroid.Name} nearby on our path: {Name}");

            // If we're following a pathfinder path, recalculate the path because we're near an asteroid
            if (IsMobile && ShipType != ConfigData.ShipTypes.Queen)
            {
                if (!IsFollowingPath && !HasTargetCoordinates)
                {
                    MoveToPoint(GetPosition(), true);
                }
                else
                {
                    MoveToPoint(FinalDestination, true);
                }

                if (!_isDoubleCheckingForAsteroids)
                {
                    _isDoubleCheckingForAsteroids = true;
                    _asteroidDoubleCheckTimer.Reuse(1, NearbyAsteroidDoubleCheck, true);
                    Level.AddTimer(_asteroidDoubleCheckTimer);
                }

                //InvokeRepeating(nameof(NearbyAsteroidDoubleCheck), 1f, 1f);
            }

        }
        /// <summary>
        /// Called on a delay from FoundNearbyAsteroid to check the pathfinding again in hopes of avoiding running into the asteroid's new position shortly after detecting it
        /// </summary>
        public void NearbyAsteroidDoubleCheck()
        {
            if (NearbyAsteroids.Count > 0)
            {
                //Debug.Log($"There are still {NearbyAsteroids.Count} asteroids near {Name}, double checking the pathfinding");
                MoveToPoint(FinalDestination);
            }
            else
            {
                Level.CancelTimer(_asteroidDoubleCheckTimer);
                _isDoubleCheckingForAsteroids = false;
                //CancelInvoke(nameof(NearbyAsteroidDoubleCheck));
            }
        }
        public void LeftNearbyAsteroid(CollisionAsteroid asteroid)
        {
            NearbyAsteroids.Remove(asteroid);
        }

        public int _retries = 0;
        private bool _tryingToFindPathAgain;
        private void MergePathfindingPaths()
        {
            //if (PrintDebugImage)
            //{
            //    DebugGrid.DebugGridAsImage(new Vector2Int(DebugStartNode.x, DebugStartNode.y), new Vector2Int(DebugEndNode.x, DebugEndNode.y), DebugNodes, 4, this);
            //}
            if (PathfindingValue != null && PathfindingValue.Points.Count > 0)
            {
                _retries = 0;
                //float start = Time.realtimeSinceStartup;
                //Debug.Log($"Merging pathfinding paths for {Name} with {PathfindingValue.Points.Count} points");
                //Vector2 firstPoint = PathfindingValue.Points.Take(25).OrderBy((p) => DistanceToPoint(p)).Take(10).OrderBy((p) => GetRotatedAngleToPoint(p)).First();

                //Debug.Log($"First point is {firstPoint}");
                //DestinationQueue.Clear();
                //for (int i = PathfindingValue.Points.IndexOf(firstPoint); i < PathfindingValue.Points.Count; i++)
                //{
                //    DestinationQueue.Enqueue(PathfindingValue.Points[i]);
                //}
                DestinationQueue = new Queue<Vector2>(PathfindingValue.Points);
                FinalDestination = DestinationQueue.Last();
                SetTargetCoordinates(DestinationQueue.Dequeue());
                IsFollowingPath = true;
                HasTargetCoordinates = true;
                PathfindingValue = null;
                DebugWalkablePointNodes.Clear();
                MoveMovementMarker();
                //Debug.Log($"Merged full path to destination in {(Time.realtimeSinceStartup - start) * 1000}ms");
            }
            else
            {
                //Debug.Log($"{Name} couldn't find a path to {PathfindingDestination} and so it will try again in 2 seconds");
                if (_retries < 5 && !_tryingToFindPathAgain)
                {
                    EndDestination("Could not find a path to destination");
                    _tryingToFindPathAgain = true;
                    _tryToFindPathAgainTimer.Reuse(2, TryToFindPathAgain);
                    Level.AddTimer(_tryToFindPathAgainTimer);
                    //Invoke(nameof(TryToFindPathAgain), 2);
                    _retries++;
                }

            }
            IsPathfinding = false;
        }
        private ScaledTimer _tryToFindPathAgainTimer = new ScaledTimer();
        public void TryToFindPathAgain()
        {
            MoveToPoint(PathfindingDestination);
            _tryingToFindPathAgain = false;
        }
        /// <summary>
        /// Periodically called while following a pathfinding path. Checks to see if there are any obstacles in the way and if not, cuts off the destination queue and takes a direct path
        /// </summary>
        private void CheckForDirectPath()
        {
            if (!HasObstacleInPath(FinalDestination))
            {
                //Debug.Log($"Found a direct path for {Name} to {FinalDestination}");
                //SetTargetCoordinates(FinalDestination);
                IsFollowingPath = false;
                DestinationQueue.Clear();
                //CancelInvoke(nameof(CheckForDirectPath));
            }
        }
        private void MoveMovementMarker()
        {
            if (HasMovementMarker && Squad.IsSelected)
            {
                if (HasTargetCoordinates)
                {
                    MovementMarker.transform.position = FinalDestination;
                    MovementMarker.SetActive(true);
                }
                else
                {
                    MovementMarker.SetActive(false);

                }
            }
        }
        public void SetTargetCoordinates(Vector2 v)
        {
            TargetCoordinates = v;
        }
        private void Move()
        {
            if (HasBrain && !Squad.IsUserControlled)
            {
                NNDirectionalMovement();
            }
            else
            {
                if (HasTargetCoordinates)
                {

                    MoveToTargetCoordinates();
                    //MoveAttachedSprites();
                    Squad.MoveSquadBox();
                }
                else if (HasTargetDirection)
                {
                    MoveInDirection();
                    Squad.MoveSquadBox();
                }
            }
        }
        private void NNDirectionalMovement()
        {
            if (ShouldDetonate)
            {
                if (ShipType == ConfigData.ShipTypes.Striker)
                {
                    ((Striker)this).TryToDropBombs();
                }
                else if (ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    ((YellowJacket)this).TryToDetonate();
                }
                else if (ShipType == ConfigData.ShipTypes.FireBarge)
                {
                    ((FireBarge)this).Detonate();
                }
            }
            if (Direction == 360)
            {
                Body.linearVelocity = Vector2.zero;
                IsMoving = false;
                return;
            }
            if (!HasTargetCoordinates || DistanceToPoint(TargetCoordinates) > GetHeight())
            {
                Utilities.TimedRotationDifference(this, Direction, RotationSpeed);
            }

            _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;

            //bool hitBoundaries = false;

            _tempVelocity = new Vector2((Speed * Mathf.Sin(_tempAngle)), (-1 * Speed * Mathf.Cos(_tempAngle)));

            //Vector2 unclamped = transform.localPosition;

            // This shouldn't be necessary any more because obstacles prevent ships from moving outside of bounds, not the clamping
            //Vector2 pos = GetPosition();
            //transform.localPosition = new Vector2(Mathf.Clamp(pos.x, Level.MinX, Level.MaxX), Mathf.Clamp(pos.y, Level.MinY, Level.MaxY));

            Body.linearVelocity = _tempVelocity;
            IsMoving = true;
        }
        public virtual void SetRocketFlares()
        {
            CenterRocketFlares.ForEach((flare) =>
            {
                flare.SetActive(true);
            });

            //Debug.Log($"differenceInAngleToPoint: {differenceInAngleToPoint}");
            if (HasRightRocketFlares && HasLeftRocketFlares)
            {
                //Debug.Log($"The difference in angle for {Name} is {_differenceInAngleToPoint}");
                if (_differenceInAngleToPoint > 5)
                {
                    //Debug.Log($"Moving to the left, activating right rocket flares");
                    RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(true);
                    });

                    LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    AreRocketFlaresOutOfSync = true;
                }
                else if (_differenceInAngleToPoint < -5)
                {
                    //Debug.Log($"Moving to the right, activating left rocket flares");
                    LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(true);
                    });

                    RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    AreRocketFlaresOutOfSync = true;
                }
                else if (!HasOnlySideRocketFlares) // moving straight, activate both sides unless they are side rocket flares like with the factory
                {
                    RightRocketFlares.ForEach((flare) =>
                    {
                        if (AreRocketFlaresOutOfSync)
                        {
                            flare.SetActive(false);
                        }
                        flare.SetActive(true);
                    });

                    LeftRocketFlares.ForEach((flare) =>
                    {
                        if (AreRocketFlaresOutOfSync)
                        {
                            flare.SetActive(false);
                        }
                        flare.SetActive(true);
                    });

                    AreRocketFlaresOutOfSync = false;
                }
                else // moving straight and only has side rocket flares
                {
                    RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });
                }
            }
        }
        private float _maxSpeed, _rotation;
        protected float _differenceInAngleToPoint;
        public void SetMovementVelocity()
        {


            // Set the velocity of the ship
            if (HasTargetCoordinates)
            {
                _rotation = GetDegreesTowardsPoint(TargetCoordinates);
            }
            else if (HasTargetDirection)
            {
                _rotation = TargetDirection;
            }
            else
            {
                Debug.LogWarning($"{Name} is triyng to move without target coordinates or target direction");
                return;
            }
            IsMoving = true;
            _differenceInAngleToPoint = Utilities.TimedRotationDifference(this, _rotation, RotationSpeed);

            if (_differenceInAngleToPoint != 0 || Stage.FixedUpdates % 10 == 0 || _maxSpeed != CurrentSpeed)
            {
                //_maxSpeed = Stage.IsDebugging ? CurrentSpeed * Stage.SpeedMultiplier : CurrentSpeed;
                _maxSpeed = CurrentSpeed;
                //Debug.Log($"Setting _maxSpeed to {_maxSpeed} for {Name}");
                _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
                Body.linearVelocity = new Vector2((_maxSpeed * Mathf.Sin(_tempAngle)), -(_maxSpeed * Mathf.Cos(_tempAngle)));
            }
            //_tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
            //Body.velocity = new Vector2((_maxSpeed * Mathf.Sin(_tempAngle)), -(_maxSpeed * Mathf.Cos(_tempAngle)));
            //if (ShipType != ConfigData.ShipTypes.Flagship)
            //{
            //    IsMoving = true;

            //    _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
            //    Body.velocity = new Vector2((_maxSpeed * Mathf.Sin(_tempAngle)), -(_maxSpeed * Mathf.Cos(_tempAngle)));
            //}


            if (HasRocketFlares)
            {
                SetRocketFlares();
            }



        }
        private void MoveInDirection()
        {
            SetMovementVelocity();
        }


        private void MoveToTargetCoordinates()
        {

            _tempDistance = DistanceToPoint(TargetCoordinates);
            SetMovementVelocity();

            //try
            //{
            //    bool testTrue = HasTargetEnemyShipToFollow &&
            //    !(Squad.HasCommand && _attackTypes.Contains(Squad.GetCommand().CommandType)) &&  // Squad must either not have a command or not have a command of a certain type

            //    //TargetShips.Any((ship) => ship != null && (!HasTargetEnemy || TargetEnemy.Equals(ship)) && IsShipWithinRange(ship)) // Ship must have target ships within range and they must be the target enemy or there must not be a target enemy 

            //    IsShipWithinRange(TargetEnemyShipToFollow); // Ship must be in range of the enemy ship that it's following
                
            //}
            //catch (Exception e)
            //{
            //    Debug.Log(HasTargetEnemyShipToFollow);
            //    Debug.Log(Squad);
            //    Debug.Log(Squad?.HasCommand);
            //    Debug.Log(_attackTypes.Contains(Squad.GetCommand().CommandType));
            //    Debug.Log(IsShipWithinRange(TargetEnemyShipToFollow));
            //    throw e;
            //}
            // stop if you're close enough to your destination

            if (IsCloseEnoughToTargetCoordinates(_tempDistance))
            {

                //Debug.Log($"Ship {Name} is close enough ({distance}) to the target coordinates {TargetCoordinates} and will now stop moving.");

                // $"Ship {Name} is close enough ({_tempDistance}) to the target coordinates {TargetCoordinates}"
                EndDestination();

                //int stacked = GetCountOfSameShipsBelowThisShip();
                //if (!IsBelowOtherShips())
                //{
                //    Debug.Log($"{Name} is on top of {stacked} {ShipType}s and has no ships above it");

                //}
            }

            //if any of the target ship(s) if your weapons are not dead and are within range
            else if (
                HasTargetEnemyShipToFollow &&
                !(Squad.HasCommand && Squad.HasMovementAttackType) &&  // Squad must either not have a command or not have a command of a certain type

                //TargetShips.Any((ship) => ship != null && (!HasTargetEnemy || TargetEnemy.Equals(ship)) && IsShipWithinRange(ship)) // Ship must have target ships within range and they must be the target enemy or there must not be a target enemy 

                IsShipWithinRange(TargetEnemyShipToFollow) // Ship must be in range of the enemy ship that it's following
                )
            {
                // If we're not attacking or the enemy isn't moving, or all of the enemy ships are within this ship's range
                //if (!Squad.IsAttacking || !Squad.GetCommand().Enemy.IsMoving)
                //{

                //    EndDestination($"A target ship is within our range");
                //    //SetCurrentSpeed(Squad.GetCommand().Enemy.MaxSpeed);
                //}
                //else
                //{
                //    SetCurrentSpeed(Squad.GetCommand().Enemy.MaxSpeed);
                //}
                //EndDestination($"A target ship is within our range");
                SetCurrentSpeed(TargetEnemyShipToFollow.CurrentSpeed);
                if (Level.Stage.FixedUpdates % 10 == 0 && DistanceTo(TargetEnemyShipToFollow) < HalfMaxRange)
                {
                    // $"A target ship is within our range"
                    EndDestination();
                }
                return;
            }
            if (Squad.IsMatchingSpeed && CurrentSpeed != Squad.CurrentSpeed && Squad.CurrentSpeed > 0)
            {
                SetCurrentSpeed(Squad.CurrentSpeed);
            }
            else if (!Squad.IsMatchingSpeed && CurrentSpeed != Speed)
            {
                SetCurrentSpeed(Speed);
            }


        }
        /// <summary>
        /// Either stops the ship or sets it on course to the next destination
        /// </summary>
        /// <param name="reason"></param>
        private void EndDestination(string reason = null)
        {
            if (DestinationQueue.Count > 0)
            {
                SetTargetCoordinates(DestinationQueue.Dequeue());
                //Debug.Log($"There are more target coordinates, not ending movement: {TargetCoordinates}");
            }
            //else if (HasTargetEnemy)
            //{
            //    TargetCoordinates = TargetEnemy.GetPosition();
            //}
            else
            {
                StopMoving(reason);
            }
        }
        /// <summary>
        /// if the distance to the point is less than the turning radius and this isn't a bombing ship that's right next to it's target
        /// </summary>
        /// <param name="distance"></param>
        /// <returns></returns>
        public virtual bool IsCloseEnoughToTargetCoordinates(float distance)
        {
            return distance < ConfigData.ShipTurningRadius;
        }
        /// <summary>
        /// Stop the ship from moving at all
        /// </summary>
        /// <param name="reason"></param>
        public void StopMoving(string reason = null)
        {
            if (IsMobile)
            {
                //__LastStopReason = $"{Name} stopped at {GetPosition()} on the way to {TargetCoordinates} because of {reason} at {Age} ticks.";
                Debug.Log($"{Name} stopped at {GetPosition()} on the way to {TargetCoordinates} because of {reason}");
                SetTargetCoordinates(Vector2.zero);
                FinalDestination = Vector2.zero;
                Body.linearVelocity = Vector2.zero;
                IsMoving = false;
                ClearPreviousDesintation();
                if (HasRocketFlares)
                {
                    CenterRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                }
                MoveMovementMarker();
                //transform.position = TargetCoordinates;
                //SetToDefaultAngle();
            }

        }
        public void ClearPreviousDesintation()
        {
            HasTargetCoordinates = false;
            HasTargetDirection = false;
            TargetDirection = 0;
            if (IsFollowingPath)
            {
                IsFollowingPath = false;
                DestinationQueue.Clear();
                //CancelInvoke(nameof(CheckForDirectPath));
            }
        }
        private Vector3 _reverse = Vector3.forward * 180;
        public void SetToDefaultAngle()
        {
            if (Side == ConfigData.Configuration.AISide) // Different from Hivemind controlled because AISide is the enemy side that comes from the top of the map
            {
                //Debug.Log($"Set angle for {name} to {_reverse}");
                Transform.eulerAngles = _reverse;
                Rotation = 180;
                //if (ShipType == ConfigData.ShipTypes.Queen)
                //{
                //    throw new Exception();
                //}
            }
        }
        public void Clicked(int mouseButton, bool isCtrlClick = false)
        {
            if (!IsUserControlled && mouseButton == LevelInputManager.RightClick) // when this ship has been right clicked on and this ship *is not* user controlled
            {
                Debug.Log($"Targeted squad #{Squad.SquadNumber}");
                Level.State.GetSelectedSquads().ForEach((selectedSquad) =>
                {
                    //selectedSquad.UserTargetSquad(squad);

                    selectedSquad.UserAggressive(Squad);
                });
            }
            else if (IsUserControlled && mouseButton == LevelInputManager.LeftClick) // when this ship has been left clicked on and this ship *is* user controlled
            {
                if (!Squad.IsImmobile)
                {
                    if (isCtrlClick)
                    {
                        Level.State.AddSelectedSquad(Squad);
                    }
                    else
                    {
                        Level.State.SelectSquad(Squad);
                    }
                }

            }
        }
        public void SetCurrentSpeed(float speed, float maxSpeed = -1)
        {
            if (maxSpeed == -1)
            {
                maxSpeed = Speed;
            }
            CurrentSpeed = math.min(speed, maxSpeed);
        }


        // Combat methods
        public void ClearTargets()
        {
            Weapons.ForEach((weapon) =>
            {
                weapon.ClearTargets();
            });
        }
        private void CombatTimer()
        {
            InCombat = false;
            Level.CancelTimer(_combatTimerScaledTimer);
            //CancelInvoke(nameof(CombatTimer));
            _combatTimer = false;
        }
        /// <summary>
        /// Set in Create() method
        /// </summary>
        private float _maxRateOfFire, _repeatRate;
        private ScaledTimer _combatTimerScaledTimer = new ScaledTimer();
        /// <summary>
        /// Sets the combat timer. A ship is in combat if it has fired before the combat timer has expired. The timer is currently set to 5 seconds.
        /// In Combat is used for Hivemind Matchup Strategies.
        /// </summary>
        public void SetCombatTimer()
        {
            // if the combat timer already exists, clear it
            if (IsUserControlled && Level.Stage.ActivateHiveMind) // The combat timer and In Combat are only used for Hivemind Strategies so it only makes sense to use this when those are in use
            {
                if (_combatTimer)
                {
                    Level.CancelTimer(_combatTimerScaledTimer);
                    //CancelInvoke(nameof(CombatTimer));
                }

                // set the ship as in combat because it is firing
                InCombat = true;

                /* set a timer to check every 5 seconds and if the game is not paused, the ship will be out of combat
                But if the ship fires again within those 5 seconds the above code will clear the timer
                 */
                _combatTimer = true;
                _combatTimerScaledTimer.Reuse(_repeatRate, CombatTimer, true);
                //InvokeRepeating(nameof(CombatTimer), _repeatRate, _repeatRate);
            }

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;
            if (_tempCollidingThing.name == ("Selection Box"))
            {
                //Debug.Log("Hit selection box");
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            //else if (collidingThing.CompareTag("Obstacle") && BumperCollider.IsTouching(collider))
            //{
            //    Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
            //    Debug.Log($"{Name} bumper collided with {obstacle.Name}");
            //    StopMoving("Hit obstacle");
            //}
        }
        private int _oldTsv, _tsvChange;
        /// <summary>
        /// Notes the change in health and TSV for the ship and Squad command and updates the health bar for any kind of non-attacking damage the ship takes. See LogAttackingDamage() for attacking damage
        /// [TSV]
        /// </summary>
        public void LogDamage(int damage)  // [damage-method] [note]
        {
            if (Health > 0)
            {
                _oldTsv = Tsv;
                Health -= math.min(damage, Health);
                Tsv = Utilities.CalculateTsv(this);

                _tsvChange = Tsv - _oldTsv;
                FleetShip.DamageReceived += -_tsvChange;
                Squad.SavedSquad.Stats.DamageReceived += -_tsvChange;

                if (Squad.HasCommand)
                {
                    Squad.GetCommand().Tsv += _tsvChange; // subtract the TSV from the target
                }
                if (Health == 0)
                {
                    Kill(null, null, null);
                }
                else
                {
                    UpdateHealthBar();
                }
            }

        }
        private static int _targetOldTSV, _targetTSVChange;
        private static int _targetOldHealth; // [debug]
        private static ShipDamageStatus _shipDamageStatus;
        /// <summary>
        /// Logs damage to a ship from being attacked by another ship. See LogDamage() for non-attacking damage
        /// [TSV]
        /// </summary>
        public static void LogAttackingDamage(int power, Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target) // [damage-method] [note]
        {
            if (target.Health > 0)
            {
                if (target.Level.Stage.MakeShotsHarmless)
                {
                    power = 0;
                }

                attacker.ShipsHit.Add(target);
                _targetOldTSV = target.Tsv;
                _targetOldHealth = target.Health;  // [debug]
                target.Health -= math.min(power, target.Health);
                target.Tsv = Utilities.CalculateTsv(target);

                if (target.FleetShip.MineralsMinedThisLevel > 0)
                {
                    Debug.Log($"{target.Name} had {_targetOldTSV} TSV and {target.FleetShip.MineralsMinedThisLevel} minerals and now has (TSV with minerals) {target.Tsv} minus (TSV without minerals) {Utilities.CalculateTsv(target) - target.FleetShip.MineralsMinedThisLevel} TSV and {target.FleetShip.MineralsMinedThisLevel} minerals");
                    Debug.Log($"{target.Name} went from {_targetOldHealth} health to {target.Health} health after being hit by {attacker}");

                }

                if (_targetOldHealth <= target.Health) // [debug]
                {
                    Debug.LogError($"Target {target.Name} old health {_targetOldHealth} is less than or equal to new health {target.Health} after taking {power} damage from attacker {attacker.Name}");
                }

                _targetTSVChange = target.Tsv - _targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
                LogHitStats(attacker, attackerFleetShip, attackerSavedSquad, target, target.Squad, -_targetTSVChange);


                if (target.Health == 0)
                {
                    target.Kill(attacker, attackerFleetShip, attackerSavedSquad);

                    if (attacker != null)
                    {
                        attacker.Level.State.ShipDamageStatuses[attacker.Side - 1].Remove(attacker.Level.State.GetShipDamageStatus(attacker.Side, target));
                    }

                }
                else
                {
                    if (target.Level.Stage.IsTrainingNueralNetwork)
                    {
                        target.RLHealth = target.Health / target.MaxHealth;
                    }
                    target.UpdateHealthBar();
                    if (attacker != null)
                    {
                        _shipDamageStatus = target.Level.State.GetShipDamageStatus(attacker.Side, target);
                        _shipDamageStatus.Health = target.Health;
                    }

                }
            }
        }
        private static bool _isFriendlyFire;
        private static int[] _initialTsv;
        private static float _percentageTsvDestroyed;
        /// <summary>
        /// Logs the stats to the fleet ships, saved squads, and commands of the shooter and the target 
        /// </summary>
        protected static void LogHitStats(Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target, Squad targetSquad, int tsvLoss) // [stats-method] [note]
        {
            if (tsvLoss < 0)
            {
                Debug.LogError($"The tsv loss for target {target.Name} is negative when it should be positive: {tsvLoss}");
            }
            //Debug.Log($"Logging hit stats");
            // tsvChange is a negative number, -tsvChange is a positive number

            _isFriendlyFire = false; // So far, friendly fire can only occur if a Fire Barge blows up and kills its own side's ships
            if (attackerFleetShip.Side != target.Side)
            {
                attackerFleetShip.DamageDone += tsvLoss;
                //Debug.Log($"{attackerFleetShip.Name} inflicted {tsvLoss} TSV Loss on {targetSquad.Name}");
                //Debug.Log($"shooter {shooter}");
                //Debug.Log($"squad {shooter.Squad}");
                //Debug.Log($"saved Squad {shooter.Squad.SavedSquad}");
                //Debug.Log($"stats {shooter.Squad.SavedSquad.Stats}");
                attackerSavedSquad.Stats.DamageDone += tsvLoss;
            }
            else
            {
                if (attacker.KillerFleetShip != null) // someone killed the ship that damaged this ship. (e.g. a Bumblebee killing a Fire Barge that exploded and killed this ship) The killer should receive stats for the damage
                {
                    _isFriendlyFire = true;
                    //Debug.Log($"{shooter.Killer.Name} has killed {shooter.Name} who has in turn damaged {target.Name} on the same side. {shooter.Killer.Name} has done {tsvChange} additional damage");
                    attacker.KillerFleetShip.DamageDone += tsvLoss;
                    attacker.KillerSavedSquad.Stats.DamageDone += tsvLoss;

                    if (attacker.Killer != null && attacker.Killer.Squad.HasCommand)
                    {
                        attacker.Killer.Squad.GetCommand().Tsv += tsvLoss; // add the TSV to the shooter
                    }
                }
               

            }


            if (attacker != null && attacker.Squad.HasCommand)
            {
                attacker.Squad.GetCommand().Tsv += tsvLoss * (_isFriendlyFire ? -1 : 1); // multiply by -1 and add the TSV to the shooter if it's friendly fire
                                                                                     // add the positive number if it's not friendly fire
            }


            if (target != null)
            {
                //Debug.Log($"{target.Name} has been hit by {attacker.Name} and so {target.FleetShip.Name} and {target.Squad.SavedSquad.Name} will increase damage received");
                target.FleetShip.DamageReceived += tsvLoss;
                target.Squad.SavedSquad.Stats.DamageReceived += tsvLoss;

                if (targetSquad.HasCommand)
                {
                    targetSquad.GetCommand().Tsv += -tsvLoss; // add the negative TSV to the target command because it took damage
                }

                if (target.Stage.IsTrainingNueralNetwork)
                {
                    _initialTsv = target.Level.State.InitialTsv;
                    Debug.Log($"Initial TSV: {_initialTsv[0]}, {_initialTsv[1]}");
                    _percentageTsvDestroyed = (float)Math.Round(((double)tsvLoss / _initialTsv[target.Side - 1]), 3);
                    Debug.Log($"{attacker.Name} destroyed {_percentageTsvDestroyed}  {tsvLoss} / {_initialTsv[target.Side - 1]} of the total initial tsv of the enemy");
                    //target.Brain.AddReward(-_percentageTsvDestroyed);

                    //if (attacker != null)
                    //{
                    //    attacker.Brain.AddReward(_percentageTsvDestroyed);
                    //}
                }

            }
            else if (targetSquad != null)
            {
                Debug.LogException(new Exception($"There was {tsvLoss} damage done by {attacker.Name} but the target is null. The target squad got stats though."));
                targetSquad.SavedSquad.Stats.DamageReceived += tsvLoss;
            }
            else
            {
                Debug.LogException(new Exception($"There was {tsvLoss} damage done by {attacker.Name} but the target is null and the targetSquad is null. "));
            }


        }
        /// <summary>
        /// Add kill stats for the the killer
        /// </summary>
        protected void LogKillerStats(FleetShip killerFleetShip, SavedSquad killerSavedSquad) // [stats-method] [note]
        {
            killerFleetShip.Kills++;
            killerSavedSquad.Stats.Kills++;
        }
        protected void LogKilledStats() // [stats-method]
        {
            if (Level.Stage.ReplaceDeadShips && !IsCarrierShip && !IsMinionShip && Squad.SavedSquad.HasBeenSavedToStorage)
            {
                FleetShip.IsDead = true;
            }
            Squad.SavedSquad.Stats.ShipsLost++;
            FleetShip.MineralsMinedThisLevel = 0;
        }
        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;

            if (_tempCollidingThing.name == ("Selection Box") && IsUserControlled)
            {
               Stage.Selector.DeselectShip(this);
            }
        }
        public void EndKill()
        {
            Kill(null, null, null, true);
        }
        private List<Weapon> _weapons;
        private Carrier _nextCarrier;
        private CarrierShip _carrierShip;
        public void KilledShip(Ship victim)
        {
            LastKilled = Time.frameCount;
            Weapons.ForEach(weapon =>
            {
                weapon.ShipsWithinRange.Remove(victim.Id);
                weapon.HasCachedChanged = true;
            });
        }
        //public bool IsLastShipOnSide()
        //{
        //    return Level.State.GetShips(Side).Count == 1;
        //}
        public virtual void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false) // [kill method] [stats-method] [note]
        {
            if (!IsDead)
            {
                IsDead = true;
                //Debug.Log($"Killing ship {Name} with size Factor {ConfigData.GetShipSizeFactor(ShipType)}");
                if (IsPathfinding)
                {
                    Debug.Log($"{Name} got killed while pathfinding on #{PathfindingThread}");
                }
                if (!endKill)
                {
                    DropExplosionAnimation();

                    if (killerFleetShip != null)
                    {
                        if (killer != null)
                        {
                            killer.KilledShip(this);
                        }
                        LogKillerStats(killerFleetShip, killerSavedSquad);
                    }


                    if (ShipType != ConfigData.ShipTypes.Beacon) // Losing a beacon doesn't count as losing a ship
                    {
                        LogKilledStats();
                    }

                    if (HasUserFogOfWarVision)
                    {
                        FogOfWarVision.Kill(0, false);
                    }

                    if (WeaponsThatHaveUsWithinRange.Count > 0)
                    {
                        _weapons = WeaponsThatHaveUsWithinRange.ToList();
                        for (_tempIndex = 0; _tempIndex < WeaponsThatHaveUsWithinRange.Count; _tempIndex++)
                        {
                            _weapons[_tempIndex].ShipsWithinRange.Remove(this.Id);
                        }
                    }
                    Squad.HasMovedBox = false;
                    Squad.MoveSquadBox();
                }

                //Debug.Log($"Squad {Squad.Name} ship count before {Name} has been removed (for dying): {Squad.GetShips().Count}");
                Level.State.RemoveShip(this);
                Squad.RemoveShip(this);
                //Debug.Log($"Squad {Squad.Name} ship count after {Name} has been removed (for dying): {Squad.GetShips().Count}");

                // If this is a carrier, get all strikers that belonged to this carrier and mark the last spot the carrier was at
                if (ShipType == ConfigData.ShipTypes.Carrier)
                {
                    _nextCarrier = (Carrier)Level.State.GetHumanShips().FirstOrDefault((s) => s.ShipType == ConfigData.ShipTypes.Carrier);
                    if (_nextCarrier != null)
                    {

                        Level.State.GetHumanShips().ForEach((ship) =>
                        {
                            if (ship.Squad.IsCarrierSquad)
                            {
                                _carrierShip = (CarrierShip)ship;

                                if (_carrierShip.Carrier == this)
                                {
                                    _carrierShip.Carrier = _nextCarrier;
                                }
                            }
                        });
                    }
                    else
                    {
                        Squad.GetShips().Where((ship) => ship.ShipType == ConfigData.ShipTypes.Striker).ToList().ForEach((ship) => {
                            ((Striker)ship).LastCarrierPosition = GetPosition();
                        });
                    }

                }

                // If there are any projectiles in flight, let them know the ship is dead
                if (ProjectilesInFlight.Count > 0)
                {
                    ProjectilesInFlight.ToList().ForEach((projectile) =>
                    {
                        projectile.ShipIsDead = true;
                        //Debug.Log($"Letting projectile ({projectile.Name}) know that its ship ({Name}) is dead.");
                    });

                }

                if (Squad.GetShips().Count == 0)
                {
                    //Debug.Log($"Killing squad {Squad.Name} because it doesn't have any ships left");
                    Squad.Kill(endKill);
                }
                else
                {
                    //Debug.Log($"Not killing squad {Squad.Name} because it has {Squad.GetShips().Count} ships left");
                    Squad.SetOffsets();
                }
                //Debug.Log($"{Name} has been killed and will be returned");
                Level.CancelTimer(_asteroidDoubleCheckTimer);
                Level.CancelTimer(_combatTimerScaledTimer);
                if (HasWeapons)
                {
                    Weapons.ForEach(weapon =>
                    {
                        weapon.CancelTimer();
                    });
                }
                Deactivate();


            }
        }
        public override void Deactivate()
        {
            //CancelInvoke();
            //StopAllCoroutines();
            //if (HasMovementMarker)
            //{
            //    MovementMarker.SetActive(false);
            //}
            //gameObject.SetActive(false);
            Body.linearVelocity = Vector2.zero;
            base.Deactivate();
            //CancelInvoke();
            StopAllCoroutines();

            if (HasWeapons)
            {
                Weapons.ForEach(weapon =>
                {
                    weapon.Deactivate();
                    if (IsUserControlled && weapon.HasRangeCircle)
                    {
                        weapon.RangeCircle.SetActive(false);
                    }
                });
            }
            if (!IsUserControlled)
            {
                HiveMindVision.Deactivate();
            }
            if (HasProximityCollider)
            {
                ProximityCollider.Deactivate();
            }

            if (!Stage.IsTraining)
            {
                SortingGroup.enabled = false;
                MiniMapIcon.SetActive(false);

                if (HasRocketFlares)
                {

                    CenterRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                }

                if (HasMovementMarker)
                {
                    MovementMarker.SetActive(false);
                }
                HealthBar.SetActive(false);
            }
            else
            {
                if (Stage.IsRendering)
                {
                    HealthBar.SetActive(false);
                }
            }


        }
        public override void Activate()
        {
            //gameObject.SetActive(true);

            base.Activate();

            if (IsUserControlled)
            {
                if (Level.ActivateFogOfWar)
                {
                    FogOfWarVision.Activate();
                }
            }
            else
            {
                HiveMindVision.Activate();
            }
            if (HasProximityCollider)
            {
                ProximityCollider.Activate();
            }
            if (HasWeapons)
            {
                Weapons.ForEach(weapon =>
                {
                    weapon.Activate();
                });
            }
            if (Stage.IsRendering)
            {
                HealthBar.SetActive(true);
            }
            if (!Stage.IsTraining)
            {
                SortingGroup.enabled = true;
                MiniMapIcon.SetActive(true);
            }
        }
        private int _maxLoops;
        /// <summary>
        /// Returns the target enemy ship to follow for this ship. The Target enemy ship will be the first in the targeting queue for this ship's squad's command. This is different from which ship its weapons are targeting
        /// </summary>
        /// <returns></returns>
        public Ship SetAndGetTargetEnemy()
        {
            _tempIndex = 0;
            try
            {
                _maxLoops = math.max(Squad.GetCommand().EnemySquad.GetShips().Count, 10);
            }
            catch (NullReferenceException e)
            {
                Debug.LogError($"Squad: {Squad}, Command: {Squad?.GetCommand()}, EnemySquad: {Squad?.GetCommand()?.EnemySquad}, enemy ship count: {Squad?.GetCommand()?.EnemySquad?.GetShips().Count}");

                throw e;
            }
            while (!HasTargetEnemyShipToFollow && _tempIndex < _maxLoops) // [note] the loop check should be removed if no longer needed
            {
                _tempIndex++;

                if (Squad.GetCommand().TargetingQueue.Count == 0)
                {
                    if (Squad.GetCommand().EnemySquad.IsGrowingSquad)
                    {
                        Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                    }
                    Squad.GetCommand().TargetingQueue = new Queue<Ship>(Squad.GetCommand().OriginalQueue);
                }
                TargetEnemyShipToFollow = Squad.GetCommand().TargetingQueue.Dequeue();
                if (TargetEnemyShipToFollow.IsDead)
                {
                    Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                }
                //try
                //{
                //    if (Squad.GetCommand().TargetingQueue.Count == 0)
                //    {
                //        if (Squad.GetCommand().EnemySquad.IsGrowingSquad)
                //        {
                //            Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                //        }
                //        Squad.GetCommand().TargetingQueue = new Queue<Ship>(Squad.GetCommand().OriginalQueue);
                //    }
                //    TargetEnemyShipToFollow = Squad.GetCommand().TargetingQueue.Dequeue();
                //    if (!TargetEnemyShipToFollow.IsDead)
                //    {
                //        Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                //    }
                //}
                //catch (Exception e)
                //{
                //    Debug.Log($"Squad: {Squad}");
                //    Debug.Log($"Command: {Squad?.GetCommand()}");
                //    Debug.Log($"TargetingQueue Count: {Squad?.GetCommand()?.TargetingQueue.Count}");
                //    Debug.Log($"TargetingQueue Content: {Utilities.ListToString(Squad?.GetCommand()?.TargetingQueue?.ToList())}");
                //    Debug.Log($"Enemy: {Squad?.GetCommand()?.EnemySquad?.Name}");
                //    Debug.Log($"TargetEnemyShipToFollow: {TargetEnemyShipToFollow}");
                //    throw e;
                //}

                //if (Squad.GetCommand().TargetingQueue.Count == 0)
                //{
                //    if (Squad.GetCommand().EnemySquad.IsGrowingSquad)
                //    {
                //        Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                //    }
                //    Squad.GetCommand().TargetingQueue = new Queue<Ship>(Squad.GetCommand().OriginalQueue);
                //}
                //TargetEnemyShipToFollow = Squad.GetCommand().TargetingQueue.Dequeue();


                //Debug.Log($"{Name} doesn't have target ships so it's moving towards the target ship in the squad, {TargetEnemy.Name}");
            }
            if (_tempIndex == _maxLoops)
            {
                Debug.Log($"Squad: {Squad}");
                Debug.Log($"Command: {Squad?.GetCommand()}"); 
                Debug.Log($"TargetingQueue Count: {Squad?.GetCommand()?.TargetingQueue.Count}");
                Debug.Log($"TargetingQueue Content: {Utilities.ListToString(Squad?.GetCommand()?.TargetingQueue?.ToList())}");
                Debug.Log($"Enemy: {Squad?.GetCommand()?.EnemySquad?.Name}");
                Debug.Log($"TargetEnemyShipToFollow: {TargetEnemyShipToFollow}");
                Debug.Log($"_maxLoops: {_maxLoops}");
                //Debug.Log($"Make Targeting Queue: {Squad?.Command?.MakeTargetingQueue()}");
                Debug.LogException(new Exception($"Hit loop limit for getTargetEnemy()"));
            }
            return TargetEnemyShipToFollow;

        }




        /* Range and distance methods */
        static public RaycastHit2D BoxCastDebug(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance, int mask)
        {

            RaycastHit2D hit = Physics2D.BoxCast(origin, size, angle, direction, distance, mask);
            Debug.Log($"{hit}, {hit.collider}, {hit.transform}");

            //Setting up the points to draw the cast
            Vector2 p1, p2, p3, p4, p5, p6, p7, p8;
            float w = size.x * 0.5f;
            float h = size.y * 0.5f;
            p1 = new Vector2(-w, h);
            p2 = new Vector2(w, h);
            p3 = new Vector2(w, -h);
            p4 = new Vector2(-w, -h);

            Quaternion q = Quaternion.AngleAxis(angle, new Vector3(0, 0, 1));
            p1 = q * p1;
            p2 = q * p2;
            p3 = q * p3;
            p4 = q * p4;

            p1 += origin;
            p2 += origin;
            p3 += origin;
            p4 += origin;

            Vector2 realDistance = direction.normalized * distance;
            p5 = p1 + realDistance;
            p6 = p2 + realDistance;
            p7 = p3 + realDistance;
            p8 = p4 + realDistance;

            //Drawing the cast
            Color castColor = hit ? Color.red : Color.green;
            Debug.DrawLine(p1, p2, castColor, 15);
            Debug.DrawLine(p2, p3, castColor, 15);
            Debug.DrawLine(p3, p4, castColor, 15);
            Debug.DrawLine(p4, p1, castColor, 15);

            Debug.DrawLine(p5, p6, castColor, 15);
            Debug.DrawLine(p6, p7, castColor, 15);
            Debug.DrawLine(p7, p8, castColor, 15);
            Debug.DrawLine(p8, p5, castColor, 15);

            Debug.DrawLine(p1, p5, Color.grey, 15);
            Debug.DrawLine(p2, p6, Color.grey, 15);
            Debug.DrawLine(p3, p7, Color.grey, 15);
            Debug.DrawLine(p4, p8, Color.grey, 15);
            if (hit)
            {
                Debug.DrawLine(hit.point, hit.point + hit.normal.normalized * 0.2f, Color.yellow, 15);
            }

            return hit;
        }
        public bool HasObstacleInPath(Vector2 destination)
        {
            return GetObstacleInPath(destination) != null;
        }
        public Collider2D GetObstacleInPath(Vector2 destination)
        {
            //Debug.Log($"Angle: {GetDegreesTowardsPoint(destination)}, direction: {-DirectionToPoint(destination)}, distance: {DistanceToPoint(destination)}");
            //return BoxCastDebug(GetPosition(), GetSize()*1.2f, GetDegreesTowardsPoint(destination), -DirectionToPoint(destination), DistanceToPoint(destination), ConfigData.ObstaclesLayerMask).collider;
            return Physics2D.BoxCast(GetPosition(), GetSize() * 1.2f, GetDegreesTowardsPoint(destination), -DirectionToPoint(destination), DistanceToPoint(destination), ConfigData.ObstaclesLayerMask).collider;
        }
        public bool IsShipWithinRange(Ship ship)
        {
            for (_tempIndex = 0; _tempIndex < Weapons.Count; _tempIndex++)
            {
                if (Weapons[_tempIndex].IsShipValidTarget(ship))
                {
                    return true;
                }
            }
            return false;
            //return Weapons.Any((w) => w.IsShipValidTarget(ship));
        }
        public bool CanSeeShip(Ship ship)
        {
            if (Sight > 0)
            {
                return DistanceTo(ship) <= Sight;
            }
            else
            {
                return IsShipWithinRange(ship);
            }
        }

        private int _index;
        private List<Ship> _tempShips;
        /// <summary>
        /// Loops through every ship in the enemy squad and checks if it's within range of this ship
        /// </summary>
        /// <param name="squad"></param>
        /// <returns></returns>
        public bool IsAnySquadShipWithinRange(Squad enemy)
        {
            _tempShips = enemy.GetShips();
            for (_index = 0; _index < _tempShips.Count; _index++)
            {
                if (IsShipWithinRange(_tempShips[_index]))
                {
                    return true;
                }
            }
            return false;
            //return squad.GetShips().Any((ship) => IsShipWithinRange(ship));
        }
        public bool AreAllSquadShipsWithinRange(Squad squad)
        {
            return squad.GetShips().All((ship) => IsShipWithinRange(ship));
        }
        public Vector2 GetSize()
        {
            return _size;
        }
        public float GetWidth()
        {
            //Debug.Log($"{FleetShip.Name} has a sprite width of {gameObject.GetComponent<SpriteRenderer>().bounds.size.x}");
            return _size.x;
        }
        public float GetHalfWidth()
        {
            return GetWidth() / 2;
        }
        public float GetHeight()
        {
            //Debug.Log($"{FleetShip.Name} has a sprite height of {gameObject.GetComponent<SpriteRenderer>().bounds.size.y}");
            return _size.y;
        }
        public float GetHalfHeight()
        {
            return GetHeight() / 2;
        }
        public Vector2 GetLeftMostPoint()
        {
            
            //return Utilities.RotatePointAroundPoint(GetPosition(), new Vector2(GetX() - GetHalfWidth(), GetY()), transform.eulerAngles.z);
            return new Vector2(GetX() - GetHalfWidth(), GetY());
        }
        public Vector2 GetRightMostPoint()
        {
            //return Utilities.RotatePointAroundPoint(GetPosition(), new Vector2(GetX() + GetHalfWidth(), GetY()), transform.eulerAngles.z);
            return new Vector2(GetX() + GetHalfWidth(), GetY());
        }
        public Vector2 GetTopMostPoint()
        {
            //return Utilities.RotatePointAroundPoint(GetPosition(), new Vector2(GetX(), GetY() + GetHalfHeight()), transform.eulerAngles.z);
            return new Vector2(GetX(), GetY() + GetHalfHeight());
        }
        public Vector2 GetBottomMostPoint()
        {
            //return Utilities.RotatePointAroundPoint(GetPosition(), new Vector2(GetX(), GetY() - GetHalfHeight()), transform.eulerAngles.z);
            return new Vector2(GetX(), GetY() - GetHalfHeight());
        }
        private Vector2 _randomPointBounds, _basePosition, _randomPoint;
        private int _x, _y;
        private float _halfWidth, _halfHeight;
        public Vector2 GetRandomPointOnShip(Vector2 nearPosition)
        {
            if (SizeClass == 1)
            {
                return GetPosition();
            }

            _halfWidth = GetHalfWidth() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ShipType);
            _halfHeight = GetHalfHeight() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ShipType);

            _randomPointBounds = Utilities.ForceBounds(10, 10, _halfWidth, _halfHeight, -1 * _halfWidth, -1 * _halfHeight);
            _basePosition = nearPosition + Level.GetPosition();

            _randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, _randomPointBounds, Vector2.zero) + _basePosition;
            //if (!Collider.OverlapPoint(randomPoint))
            //{
            //    return Collider.ClosestPoint(randomPoint);
            //}
            while (!Collider.OverlapPoint(_randomPoint) && _tempIndex < 20)
            {
                _randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, _randomPointBounds, Vector2.zero) + _basePosition;
                _tempIndex++;
            }
            if (_tempIndex == 20)
            {
                //Debug.Log($"Could not find a random point on {Name}, looping through the whole of the ship");
                for (_x = (int) -_halfWidth; _x < _halfWidth; _x++)
                {
                    for (_y = (int) -_halfHeight; _y < _halfHeight; _y++)
                    {
                        _randomPoint = _basePosition + new Vector2(_x, _y);
                        //Debug.Log($"Checking to see if {randomPoint} is on {Name}");
                        if (Collider.OverlapPoint(_randomPoint))
                        {
                            return _randomPoint;
                        }
                    }
                }
                Debug.Log($"Could not find a random point on {Name}");
            }
            return _randomPoint;
        }
        private int _clearance;
        public int GetClearance()
        {
            _clearance = Clearance; 
            if (_clearance == 0)
            {
                Level.CalculateShipClearances();
                _clearance = Stage.ShipClearances.GetValueOrDefault(ShipType);
            }
            return _clearance;
        }
        /// <summary>
        /// Whether or not the ship is in the bounds of the map. Caches the result if it is in bounds
        /// </summary>
        /// <returns></returns>
        public bool IsInBounds()
        {
            if (!_isInBounds)
            {
                //Debug.Log($"{Name} is not in bounds yet but might be? {GetPosition() == Level.ForceBounds(GetPosition())}, {GetPosition()}, {Level.ForceBounds(GetPosition())}");
                _isInBounds = GetPosition() == Level.ForceBounds(GetPosition());
            }
            return _isInBounds;
        }


        // Utility methods
        public override string ToString()
        {
            return $"{Name} IsDead? {IsDead}";
        }
        // Uses a list of ships, not necessarily squad ships
        private static float _squadTotalHealthPercent, _shipHealthPercent;
        public static double GetAverageHealthPercent(List<Ship> ships)
        {
            _squadTotalHealthPercent = 0;
            foreach(Ship ship in ships)
            {
                _shipHealthPercent = ship.Health / ship.OriginalHealth;
                _squadTotalHealthPercent += _shipHealthPercent;
            }
            return Math.Round((_squadTotalHealthPercent / ships.Count) * 100);
        }


        // UI Methods
        public void ShowWeaponRanges()
        {
            Turrets.ForEach((turret) =>
            {
                turret.ShowRange();
            });
        }
        public void HideWeaponRanges()
        {
            Turrets.ForEach((turret) =>
            {
                turret.HideRange();
            });
        }
        private float _healthPercent;
        public void UpdateHealthBar()
        {
            if (Level.Stage.IsRendering)
            {
                _healthPercent = (float)Math.Round((double)((double)Health / MaxHealth), 2);
                //Debug.Log($"{Name} health: {healthPercent}% MaxHealth: {MaxHealth}");
                _healthBarFiller.localScale = new Vector2(_healthPercent, _healthBarFiller.localScale.y);
                //_healthBarFiller.sizeDelta = new Vector2(healthPercent, _healthBarFiller.sizeDelta.y);
                if (_healthPercent > .5f)
                {
                    _healthBarFillerSprite.color = ConfigData.GetUIColor("good");
                }
                else if (_healthPercent > .25f && _healthPercent <= .50f)
                {
                    _healthBarFillerSprite.color = ConfigData.GetUIColor("medium");
                }
                else if (_healthPercent <= .25f)
                {
                    _healthBarFillerSprite.color = ConfigData.GetUIColor("bad");
                }
            }
            
        }
        /// <summary>
        /// Spawns the ship explosion and shattered ship 
        /// </summary>
        protected virtual void DropExplosionAnimation()
        {
            if (!Stage.IsTraining)
            {
                ShipExplosion.transform.parent = Level.Map.Transform;
                ShipExplosion.transform.localPosition = GetPosition();
                ShipExplosion.transform.eulerAngles = Vector3.forward * Rotation;
                //ShipExplosion.GetComponent<ShipExplosionAnimation>().Play();
                ShipExplosion.SetActive(true);

                if (Level.Stage.ActivateAudio && HasShipExplosionSoundEffect)
                {
                    ShipExplosionSoundEffect.Play();
                }

                if (HasRemainsShip)
                {
                    //Debug.Log($"Dropping remains for {Name}");
                    ShipRemains.Place();
                }

            }
        }
        public void ShowShipStats()
        {
            Stage.Menus.ShowShipStats(FleetShip);
        }
        private ScaledTimer _showShipStatsTimer = new ScaledTimer();
        private void OnMouseEnter()
        {
            if (!ConfigData.SpawnedOnlyShipTypes.Contains(ShipType) && !Stage.IsTraining && ShipType != ConfigData.ShipTypes.HumanTarget)
            {
                _showShipStatsTimer.Reuse(1, ShowShipStats);
                Level.AddTimer(_showShipStatsTimer);
                //Invoke(nameof(ShowShipStats), 1);
            }
        }

        private void OnMouseExit()
        {
            Level.CancelTimer(_showShipStatsTimer);
            //CancelInvoke(nameof(ShowShipStats));
            if (!Stage.IsTraining)
            {
                Stage.Menus.ShipInfoBox.SetActive(false);
            }

        }


    }

}

