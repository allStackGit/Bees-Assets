using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;
using Assets.Scripts.Settings;
using Assets.Scripts.Entities;
using Assets.Scripts.Level;
using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Server;
using Unity.MLAgents;
using UnityEngine.UIElements;
using System.Reflection.Emit;
using Unity.Mathematics;
using System.IO;
using NUnit;
using System.Threading;

namespace Assets.Scripts.Entities.Ships
{
    public class Ship : Entity
    {
        public bool ShowDebug;
        public int Health, MaxHealth, OriginalHealth, OriginalTsv, Sight, AdditionalTsv, Clearance, MaxRange, HalfMaxRange;
        public float SizeClass, ProjectileValue, Speed, SpecialFirePower, CurrentSpeed;
        public GameObject ShipExplosion, HealthBar, MiniMapIcon, ShipAnimation;
        public Vector2 TargetCoordinates, FinalDestination, OffsetFromCenter; // the coordinates of where the ship should go, and it's offset from the center of the squad
        public Squad Squad, MotherSquad;
        public float DefaultAngle, TargetDirection;
        public long LastKilled;
        public FleetShip FleetShip = null;
        public string ShipType, Name;
        public bool FireAtFrontOfShip, InCombat, IsFollowingPath, CannotChangeMovementOrders, IsSpawnedShip;
        public Vision Vision;
        public SpriteRenderer SpriteRenderer;
        /// <summary>
        /// A ship can be killed at some point of the frame and still exist until the end of the frame. Check this to see if a ship is dead but not yet destroyed.
        /// </summary>
        public bool IsDead;
        /// <summary>
        /// This has the same side as the user and the user has a controller
        /// </summary>
        public bool IsUserControlled;
        public bool HasBrain, IsMobile, IsHiveMindControlled, IsMinionShip, HasTargetCoordinates, IsMiningShip, IsWarpGate, HasTargetDirection, HasVision, HasProximityCollider, HasShipAnimation, HasRocketFlares, 
            HasLeftRocketFlares, HasCenterRocketFlares, HasRightRocketFlares;
        public List<Weapon> Weapons;
        public List<GameObject> ProjectilePrefabs, WeaponPrefabs, ColoredPrefabs, LeftRocketFlares, CenterRocketFlares, RightRocketFlares;
        public Brain Brain = null;
        public Queue<Vector2> DestinationQueue = new Queue<Vector2>();
        public List<CollisionAsteroid> NearbyAsteroids = new List<CollisionAsteroid>();
        public List<Turret> Turrets = new List<Turret>();
        /// <summary>
        /// Used to detect other ships near this ship if this ship doesn't have a ranged weapon. Used on Strikers, Fire Barges, and Yellow Jackets to detect when they're near targets
        /// </summary>
        public ShipProximityCollider ProximityCollider;
        /// <summary>
        /// Controls the animation and recoloring of sprites if the ship has an animation
        /// </summary>
        public ShipAnimationController ShipAnimationController;
        //public Stack<Vector2> PastLocations = new Stack<Vector2>();
        public float RotationSpeed;
        /// <summary>
        /// The ship that this ship is following after in order to target it. The primary enemy ship. This is NOT necessarily the ship that this ship is firing at. The weapon(s) have that information
        /// </summary>
        public Ship TargetEnemyShipToFollow;
        public bool IsCloseEnoughToTargetEnemyShipToFollow;



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




        // [tsv-calculation] [note]
        public float Firepower => HasWeapons ? Weapons.Sum(w => w.Firepower) : SpecialFirePower;
        public float DamagePerSecond => Turrets.Sum(t => t.DamagePerSecond);
        public int Tsv => Utilities.CalculateTsv(this);
        public string ShipTypeLetter => Utilities.ConvertShipNameToType(ShipType);
        public bool HasWeapons => Weapons.Count > 0;
        /// <summary>
        /// Does this ship have ship(s) that it's weapon(s) are targeting?
        /// </summary>
        public bool HasWeaponsTargetShips => WeaponsTargetShips.Count > 0;
        /// <summary>
        /// Whether or not the ship has target coordinates. If it does, it hasn't reached the destination
        /// </summary>
        public bool HasReachedDestination => !HasTargetCoordinates;
        public bool IsMoving => Body.velocity != Vector2.zero;
        public bool IsCarrierShip => ShipType == "Striker" || ShipType == "Drone";
        public string ShootingStrategy => HasBrain ? RLShootingStrategy : Squad.GetShootingStrategy();
        /// <summary>
        /// A list of all the ships that this ship's weapons are targeting
        /// </summary>
        public List<Ship> WeaponsTargetShips => HasWeapons ? Weapons.Select((w) => w.TargetShip).Where((s) => s != null).ToList() : new List<Ship>();
        /// <summary>
        /// A list of all the ships that are within range of this ship's weapon(s)
        /// </summary>
        public List<Ship> ShipsWithinRange => HasWeapons ? Weapons.Select((w) => w.ShipsWithinRange).Aggregate(new HashSet<Ship>(), (list, current) => {
            list.UnionWith(current);
            return list;
        }).ToList() : new List<Ship>();
        public bool HasCommand => Squad.HasCommand;
        /// <summary>
        /// Means the a ship has a command, that command has live enemies, and this ship is following after one of those enemies. This is seperate from the ship(s) that this ship's weapon(s) are targeting
        /// </summary>
        public bool HasTargetEnemyShipToFollow => TargetEnemyShipToFollow != null  && !TargetEnemyShipToFollow.IsDead;


        private bool _combatTimer;
        private Transform _healthBarFiller;
        private SpriteRenderer _healthBarFillerSprite;
        private Vector2 _size;


        // Test variables
        public string __Strategy, __Squad, __SquadStatus, __CommandStatus, __LastStopReason, __EnemySquad, __TargetEnemyShipToFollow;
        public Vector2 __CommandDestination, __Velocity, __TargetCoordinates;
        public float __Firepower, __DamagePerSecond, __CurrentSpeed, __DegreesToTargetCoordinates, __DistanceToTargetCoordinates, __TurningRadius;
        public long __Tsv, __CommandTsv;
        public bool __HasReachedDestination, __SquadHasReachedDestination;
        public List<Ship> __WeaponTargetShips, __SquadShips, __NearbyShips, __ShipsWarpingHere;
        public List<string> __ShipsWithinRangeOfWeapons, __PastCommands, __BannedStrats, __DamageStatuses, __CommandTargetingQueue, __NearbyAsteroids, __HivemindShips;
        public int __Clearance;
        //public List<Vector2> __PastLocations;


        // Neural network
        public int Direction;
        public bool ShouldDetonate;
        public string RLShootingStrategy;
        public float RLSide;
        public float RLHealth;
        public float RLShipType;

        protected virtual void UpdateDebugProperties()
        {
            __Strategy = $"{Squad?.Command?.Strategy?.Name} - {Squad?.Command?.OutcomeId}";
            __EnemySquad =  Squad.HasEnemy ? Squad.Command.EnemySquad.Name : "-";
            __WeaponTargetShips = WeaponsTargetShips;
            __ShipsWithinRangeOfWeapons = ShipsWithinRange.Select((ship) => ship.Name).ToList();
            __Squad = Squad.Name;
            __SquadStatus = Squad.Status;
            //__CommandStatus = Squad.HasCommand ? Squad.Comd.Status : "-";
            __CommandDestination = Squad.HasCommand ? Squad.Command.GetDestination() : Vector2.zero;
            __TargetCoordinates = TargetCoordinates;
            if (IsMobile)
            {
                __Velocity = Body.velocity;
            }
            __Firepower = Firepower;
            __Tsv = Tsv;
            __DamagePerSecond = DamagePerSecond;
            __CommandTsv = Squad.HasCommand ? Squad.Command.Tsv : 0;
            __PastCommands = Squad.PastCommands.Select((c) => $"Command #{c.OutcomeId} - {c.Strategy.Name} against {c.Enemy} ended with {c.Tsv}" +
            $" TSV due to \"{c.FinalizationCause}\" and took {c.Age} ticks").ToList();

            __HasReachedDestination = HasReachedDestination;
            __SquadHasReachedDestination = Squad.HasReachedDestination;
            __SquadShips = Squad.GetShips();
            __BannedStrats = Squad.BannedStrats.ToList();
            __DamageStatuses = Squad.DamageSentToEnemyShipsBySquad.Select((ds) => $"{ds.TotalDamageSentToShip} damage sent to {ds.Ship.Name} against {ds.Health} health. Current health: {ds.Ship.Health}").ToList();
            __TargetEnemyShipToFollow = HasTargetEnemyShipToFollow ? $"Following {TargetEnemyShipToFollow.Name} at {TargetEnemyShipToFollow.GetPosition()}" : "None";
            __CommandTargetingQueue = Squad.HasCommand && Squad.Command.HasEnemy ? Squad.Command.TargetingQueue.Select((ship) =>  ship.Name).ToList() : new List<string>();
            __CurrentSpeed = CurrentSpeed;
            __NearbyAsteroids = NearbyAsteroids.Select((a) => a.Name).ToList();
            __DegreesToTargetCoordinates = GetDegreesTowardsPoint(TargetCoordinates);
            __DistanceToTargetCoordinates = DistanceToPoint(TargetCoordinates);
            __TurningRadius = ConfigData.ShipTurningRadius;
            __NearbyShips = HasProximityCollider ? ProximityCollider.NearbyEnemyShips.ToList() : new List<Ship>();
            __HivemindShips = Level.GetState().GetShipsVisibleToHiveMind(Side).Select(s => s.ToString()).ToList();
            __Clearance = GetClearance();

            if (ShipType == "Warp Gate")
            {
                __ShipsWarpingHere = ((WarpGate)this).ShipsWarpingHere.ToList();
            }


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
        public virtual void Setup(LevelStage level, long id, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter) // [tsv-calculation]
        {
            //Debug.Log($"Setting up ship IsCarrierShip: {IsCarrierShip}");

            Id = id;
            Squad = squad;
            Side = squad.Side;
            Level = level;
            FleetShip = fleetShip;
            ShipType = FleetShip.Type;
            OffsetFromCenter = offsetFromCenter;
            Body = GetComponent<Rigidbody2D>();
            Collider = GetComponent<Collider2D>();

            IsUserControlled = Side == ConfigData.Configuration.UserSide && Level.HasPlayer;

            if (!IsUserControlled)
            {
                IsHiveMindControlled = true;
            }

            Transform brain = transform.Find("Brain");

            MaxHealth = FleetShip.MaxHealth;
            if (brain != null && Level.ActivateBrains)
            {
                //Debug.Log($"Found a brain for {Name}, {brain}");
                Brain = brain.GetComponent<Brain>();
                Brain.Setup(this);
                HasBrain = true;

                RLSide = Side / 2;
                RLHealth = Health / MaxHealth;
                RLShipType = (float)Utilities.ShipTypeToInt[ShipTypeLetter] / Utilities.ShipNamesAndTypes.Count;
            }

            if (FleetShip.Id < 0)
            {
                IsSpawnedShip = true;
            }

            ShipStatBlock shipStats = ConfigData.GetShipInfo(fleetShip.Type);
            Health = shipStats.Health;
            OriginalHealth = Health;
            Clearance = Level.ShipClearances.GetValueOrDefault(ShipType);

            Name = $"{ShipType} #{Id}";
            gameObject.name = Name;
            _healthBarFiller = HealthBar.transform.GetChild(0);
            _healthBarFillerSprite = HealthBar.transform.GetChild(0).GetComponent<SpriteRenderer>();

            if (squad.HasCustomColor)
            {
                Utilities.SetUIColor(MiniMapIcon, squad.Color);
            }
            else if (Side == ConfigData.Configuration.HumanSide)
            {
                Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("human"));
            }
            else if (Side == ConfigData.Configuration.BeeSide)
            {
                Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("bee"));
            }

            if (fleetShip.Type == "Striker" || fleetShip.Type == "Barge")
            {
                SpecialFirePower = shipStats.Powers[0] / 3;
            }
            else if (fleetShip.Type == "Fire Ship")
            {
                SpecialFirePower = shipStats.Powers[0] * shipStats.ProjectileValues[0];
            }
            else if (fleetShip.Type == "Yellow Jacket")
            {
                SpecialFirePower = shipStats.Powers[0] / 5;
            }
            else if (fleetShip.Type == "Carpenter Bee" || fleetShip.Type == "Factory")
            {
                IsMiningShip = true;
            }
            else if (fleetShip.Type == "Warp Gate")
            {
                IsWarpGate = true;
                Level.GetState().HasWarpGates = true;
            }
            for (int i = 0; i < shipStats.ProjectileValues.Count; i++)
            {
                string weaponType = shipStats.WeaponTypes[i];
                Weapon weapon = null;
                if (weaponType == "Turret" || weaponType == "Light Cannon" || weaponType == "Rocket Turret")
                {
                    weapon = gameObject.AddComponent<Turret>();
                }
                else if (weaponType == "Eye")
                {
                    weapon = gameObject.AddComponent<Eye>();
                }
                else if (weaponType == "Bomb")
                {
                    weapon = gameObject.AddComponent<Bomb>();
                }
                else if (weaponType == "Split Shot")
                {
                    weapon = gameObject.AddComponent<LaserBuilder>();
                }
                else if (weaponType == "Dual Cannon")
                {
                    weapon = gameObject.AddComponent<DualCannon>();
                }
                else if (weaponType == "Beam Cannon")
                {
                    weapon = gameObject.AddComponent<BeamCannon>();
                }
                else if (weaponType == "Full Ship Turret")
                {
                    weapon = gameObject.AddComponent<FullShipTurret>();
                }
                else
                {
                    Debugger.Exception($"{Name}'s weapon #{i} doesn't have a proper weapon type: {weaponType}");
                }


                if (weapon is Turret)
                {
                    //Debug.Log($"it's a turret!");
                    if (weapon is Eye)
                    {
                        ((Eye)weapon).Setup(this, weaponType, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }else if (weapon is LaserBuilder)
                    {
                        ((LaserBuilder)weapon).Setup(this, weaponType, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else if (weapon is FullShipTurret)
                    {
                        ((FullShipTurret)weapon).Setup(this, weaponType, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else
                    {
                        ((Turret)weapon).Setup(this, weaponType, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }

                }
                else
                {
                    //Debug.Log($"{weapon.GetType()} -- {typeof(Turret)}");
                    weapon.Setup(this, weaponType, shipStats.Ranges[i], shipStats.Powers[i], SpecialFirePower, shipStats.RatesOfFire[i],
                    shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i]);
                }

                Weapons.Add(weapon);
            }

            Turrets = Weapons.Where((w) => w is Turret).ToList().ConvertAll((w) => (Turret)w);


            AdditionalTsv = shipStats.AdditionalTsv;
            Sight = shipStats.Sight;
            Speed = shipStats.Speed;
            RotationSpeed = Speed * ConfigData.Configuration.RotationMultiplier;
            MaxRange = HasWeapons ? Weapons.Max((w) => w.Range) : 0;
            HalfMaxRange = MaxRange / 2;

            if (Speed > 0)
            {
                IsMobile = true;
            }

            if (ProximityCollider != null)
            {
                ProximityCollider.Setup(this, Sight);
                HasProximityCollider = true;
            }
            if (ShipAnimation != null && !Level.IsTraining)
            {
                HasShipAnimation = true;
                if (Squad.HasCustomColor)
                {
                    ShipAnimationController.RecolorAnimationSprites();
                }
            }

            if (Side == ConfigData.Configuration.HumanSide && !Level.IsTraining)
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



            OriginalTsv = Utilities.CalculateMaxTsv(this);
            _size = Collider.bounds.size;
            //squad.AddShip(this);
            Level.GetState().AddShip(this);
            SetToDefaultAngle();
            SetCurrentSpeed(Speed);


            if (IsUserControlled && Level.ActivateFogOfWar)
            {
                //Debug.Log($"Setting up vision for {Name}");
                HasVision = true;
                Vision.Setup(this);
            }
            else if (IsHiveMindControlled)
            {
                Vision.Setup(this);
                Level.GetState().HivemindShips[Side - 1].Add(Id, new HashSet<Ship>());
            }


        }
        protected void FixedUpdate()
        {
            // Debug angles from ships
            //if (Squad.IsSelected)
            //{
            //    Debug.Log(GetRotatedAngleToPoint(Level.InputManager.GetMousePosition()));
            //}
            if (PathfindingThreadComplete)
            {
                MergePathfindingPaths();
                PathfindingThreadComplete = false;
            }
            if (!Level.IsPaused)
            {
                Move();
                if (!Level.IsTraining)
                {
                    if (Side == ConfigData.Configuration.HumanSide && Level.HasPlayer && !Level.HasFoundAllBees && Level.Audio != null)
                    {
                        CheckForBees();
                    }

                    if (Level.IsDebugging || ShowDebug) // [alert] [debug] remove this for release
                    {
                        UpdateDebugProperties();
                    }
                }
            }
        }
        public void CheckForBees()
        {
            List<Ship> beeShips = Level.GetState().GetBeeShips();
            foreach(Ship bee in beeShips)
            {
                if (CanSeeShip(bee))
                {
                    if (!Level.FoundBeeTypes.Contains(bee.ShipType))
                    {
                        Level.FoundBeeTypes.Add(bee.ShipType);
                        AudioSource loop = Level.Audio.BeesLoops.GetValueOrDefault(bee.ShipType);
                        AudioSource intro = Level.Audio.BeesIntros.GetValueOrDefault(bee.ShipType);
                        if (!Level.Audio.IntroEnded)
                        {
                            Level.Audio.UnMuteSource(intro);
                        }
                        if (loop != null)
                        {
                            Level.Audio.UnMuteSource(loop);
                        }
                    }
                }
            }
        }
        public virtual void SetColor()
        {
            // set the color
            if (Squad.HasCustomColor)
            {
                //Debug.Log("Setting sprite for ship");
                float start = Time.realtimeSinceStartup;
                string status = "Loading";
                ColoredPrefabs.Insert(0, gameObject);
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                int index = 0;

                ColoredPrefabs.ForEach((prefab) =>
                {
                    Sprite prefabSprite = prefab.GetComponent<SpriteRenderer>().sprite;
                    Vector2Int size = new Vector2Int(prefabSprite.texture.width, prefabSprite.texture.height);
                    Sprite loadedSprite;
                    bool hasLoadedSprite = false;
                    if (FleetShip.HasCachedSprite)
                    {
                        loadedSprite = FleetShip.LoadCachedSprite(index, size);
                        if (loadedSprite != null)
                        {
                            prefab.GetComponent<SpriteRenderer>().sprite = loadedSprite;
                            hasLoadedSprite = true;
                        }
                    }
                    if (!hasLoadedSprite)
                    {
                        status = "Drawing";
                        Sprite shipIcon = prefabSprite;
                        int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, shipIcon);
                        Sprite recolored = Utilities.SetImageColor(Squad.Color, shipIcon, changeablePixels);
                        prefab.GetComponent<SpriteRenderer>().sprite = recolored;
                    }
                    index++;
                });

                Debug.Log($"{status} sprites for {FleetShip.Name} took {(Time.realtimeSinceStartup - start)*1000}ms");
            }
        }

        public void SetSquadName()
        {
            // Set the name of the ships with the Squad name
            Name = $"{Squad.Name}: {Name}";
            gameObject.name = Name;
        }


        // movement methods
        public void MoveToPoint(Vector2 destination, bool foundObstacle = false)
        {
            if (!CannotChangeMovementOrders)
            {
                destination = Level.ForceBounds(destination);
                if (Level.HasObstacles && IsInBounds())
                {
                    startPosition = GetPosition();
                    DestinationQueue.Clear();
                   

                    if (foundObstacle)
                    {

                        convertedStart = Level.Pathfinder.ConvertToMapCoordinates(startPosition);
                        convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                        StopMoving("Got a new destination");
                        if (!IsPathfinding)
                        {
                            Level.Pathfinder.FindPath(this, convertedStart.x, convertedStart.y, convertedDestination.x, convertedDestination.y, GetClearance());
                        }
                        else
                        {
                            Debug.Log($"{Name} is already pathfinding on {PathfindingThread} so it can't pathfind right now");
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
                        Collider2D obstacleCollider = GetObstacleInPath(destination);
                        if (obstacleCollider != null)
                        {
                            Obstacle obstacle = obstacleCollider.GetComponent<Obstacle>();
                            //Debug.Log($"{obstacle.Name} is in the way of {Name}");
                            if (!obstacle.IsCollisionAsteroid)
                            {
                                //CollisionAsteroid asteroid = (CollisionAsteroid)obstacle;
                                //if (!NearbyAsteroids.Contains(asteroid)){
                                //    NearbyAsteroids.Add(asteroid);
                                //}
                                convertedStart = Level.Pathfinder.ConvertToMapCoordinates(startPosition);
                                convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                                StopMoving("Got a new destination");

                                if (!IsPathfinding)
                                {
                                    Level.Pathfinder.FindPath(this, convertedStart.x, convertedStart.y, convertedDestination.x, convertedDestination.y, GetClearance());
                                }
                                else
                                {
                                    Debug.Log($"{Name} is already pathfinding on {PathfindingThread} so it can't pathfind right now");
                                }
                                return;
                            }

                            
                        }
                        else
                        {
                            //Debug.Log($"Direct path for {Name} to {destination}");
                        }

                    }

                    //convertedStart = Level.Pathfinder.ConvertToMapCoordinates(startPosition);
                    //convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                    //Level.Pathfinder.FindPath(this, convertedStart.x, convertedStart.y, convertedDestination.x, convertedDestination.y, GetClearance());

                }
                //else if (!IsInBounds())
                //{
                //    Debug.Log($"{Name} cannot pathfind because it's not in bounds");
                //}
                //else
                //{
                //    Debug.Log($"No obstacles in the way for {Name}");

                //}
                StopMoving("Got a new destination");
                IsFollowingPath = false;
                SetTargetCoordinates(destination);
                FinalDestination = TargetCoordinates;
                HasTargetCoordinates = true;
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
                SetTargetCoordinates(Vector2.zero);
                HasTargetCoordinates = false;
                HasTargetDirection = true;
                TargetDirection = direction;
            }

        }
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
            if (IsMobile)
            {
                if (!IsFollowingPath && !HasTargetCoordinates)
                {
                    MoveToPoint(GetPosition(), true);
                }
                else
                {
                    MoveToPoint(FinalDestination, true);
                }

                InvokeRepeating(nameof(NearbyAsteroidDoubleCheck), 1f, 1f);
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
                CancelInvoke(nameof(NearbyAsteroidDoubleCheck));
            }
        }
        public void LeftNearbyAsteroid(CollisionAsteroid asteroid)
        {
            NearbyAsteroids.Remove(asteroid);
        }
        /// <summary>
        /// Uses pathfinding (if necessary) to find the shortest path to the destination
        /// </summary>
        /// <param name="destination"></param>
        Vector2Int convertedStart, convertedDestination;
        Vector2 startPosition;
        private void MergePathfindingPaths()
        {
            //if (PrintDebugImage)
            //{
            //    DebugGrid.DebugGridAsImage(new Vector2Int(DebugStartNode.x, DebugStartNode.y), new Vector2Int(DebugEndNode.x, DebugEndNode.y), DebugNodes, 4, this);
            //}
            if (PathfindingValue != null && PathfindingValue.Points.Count > 0)
            {
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
                IsPathfinding = false;
                DebugWalkablePointNodes.Clear();
                //Debug.Log($"Merged full path to destination in {(Time.realtimeSinceStartup - start) * 1000}ms");
            }
        }
        /// <summary>
        /// Periodically called while following a pathfinding path. Checks to see if there are any obstacles in the way and if not, cuts off the destination queue and takes a direct path
        /// </summary>
        private void CheckForDirectPath()
        {
            if (!HasObstacleInPath(FinalDestination))
            {
                Debug.Log($"Found a direct path for {Name} to {FinalDestination}");
                SetTargetCoordinates(FinalDestination);
                IsFollowingPath = false;
                DestinationQueue.Clear();
                CancelInvoke(nameof(CheckForDirectPath));
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
                    if (!Squad.HasMovedBox)
                    {
                        Squad.MoveSquadBox();
                    }
                }
                else if (HasTargetDirection)
                {
                    MoveInDirection();
                    if (!Squad.HasMovedBox)
                    {
                        Squad.MoveSquadBox();
                    }
                }
            }
        }
        private void NNDirectionalMovement()
        {
            if (ShouldDetonate)
            {
                if (ShipType == "Striker")
                {
                    ((Striker)this).TryToDropBombs();
                }
                else if (ShipType == "Yellow Jacket")
                {
                    ((YellowJacket)this).TryToDetonate();
                }
                else if (ShipType == "Fire Ship")
                {
                    ((FireShip)this).Detonate();
                }
            }
            if (Direction == 360)
            {
                Body.velocity = Vector2.zero;
                return;
            }
            if (!HasTargetCoordinates || DistanceToPoint(TargetCoordinates) > GetHeight())
            {
                Utilities.TimedRotation(gameObject, Direction, RotationSpeed);
            }

            float rotation = transform.eulerAngles.z;
            float angle = (rotation - 180) * Mathf.Deg2Rad;

            //bool hitBoundaries = false;

            Vector2 velocity = new Vector2((Speed * Mathf.Sin(angle)), (-1 * Speed * Mathf.Cos(angle)));

            //Vector2 unclamped = transform.localPosition;

            // This shouldn't be necessary any more because obstacles prevent ships from moving outside of bounds, not the clamping
            //Vector2 pos = GetPosition();
            //transform.localPosition = new Vector2(Mathf.Clamp(pos.x, Level.MinX, Level.MaxX), Mathf.Clamp(pos.y, Level.MinY, Level.MaxY));

            Body.velocity = velocity;

        }
        public void SetMovementVelocity()
        {
            float maxSpeed = Level.IsDebugging ? CurrentSpeed * Level.SpeedMultiplier : CurrentSpeed;

            // Set the velocity of the ship
            float rotation;
            if (HasTargetCoordinates)
            {
                rotation = GetDegreesTowardsPoint(TargetCoordinates);

            }
            else if (HasTargetDirection)
            {
                rotation = TargetDirection;
            }
            else
            {
                return;
            }

            Utilities.TimedRotation(gameObject, rotation, RotationSpeed);
            float degrees = GetRotation() - 180;
            float angle = degrees * Mathf.Deg2Rad;

            if (HasRocketFlares)
            {
                CenterRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(true);
                });

                //Debug.Log($"Degrees: {degrees}");
                if (HasRightRocketFlares && HasLeftRocketFlares)
                {
                    if (degrees > 0)
                    {
                        //Debug.Log($"Moving to the right, activating left rocket flares");
                        LeftRocketFlares.ForEach((flare) =>
                        {
                            flare.SetActive(true);
                        });
                    }
                    else
                    {
                        //Debug.Log($"Moving to the left, activating right rocket flares");
                        RightRocketFlares.ForEach((flare) =>
                        {
                            flare.SetActive(true);
                        });
                    }
                }

            }


            Body.velocity = new Vector2((maxSpeed * Mathf.Sin(angle)), (-1 * maxSpeed * Mathf.Cos(angle)));
        }
        private void MoveInDirection()
        {
            SetMovementVelocity();
        }
        private void MoveToTargetCoordinates()
        {

            float distance = DistanceToPoint(TargetCoordinates);
            SetMovementVelocity();

            // stop if you're close enough to your destination

            if (IsCloseEnoughToTargetCoordinates(distance))
            {
                //Debug.Log($"Ship {Name} is close enough ({distance}) to the target coordinates {TargetCoordinates} and will now stop moving.");
                EndDestination($"Ship {Name} is close enough ({distance}) to the target coordinates {TargetCoordinates}");
            }

            //if any of the target ship(s) if your weapons are not dead and are within range
            else if (
                HasTargetEnemyShipToFollow &&
                !(Squad.HasCommand && (Squad.Command.Type == "Circle" || Squad.Command.Type == "Right Swipe" ||  Squad.Command.Type == "Left Swipe") ||
                Squad.Command.Type == "In and Out") &&  // Squad must either not have a command or not have a command of a certain type

                //TargetShips.Any((ship) => ship != null && (!HasTargetEnemy || TargetEnemy.Equals(ship)) && IsShipWithinRange(ship)) // Ship must have target ships within range and they must be the target enemy or there must not be a target enemy 

                IsShipWithinRange(TargetEnemyShipToFollow) // Ship must be in range of the enemy ship that it's following
                )
            {
                // If we're not attacking or the enemy isn't moving, or all of the enemy ships are within this ship's range
                //if (!Squad.IsAttacking || !Squad.Command.Enemy.IsMoving)
                //{

                //    EndDestination($"A target ship is within our range");
                //    //SetCurrentSpeed(Squad.Command.Enemy.MaxSpeed);
                //}
                //else
                //{
                //    SetCurrentSpeed(Squad.Command.Enemy.MaxSpeed);
                //}
                IsCloseEnoughToTargetEnemyShipToFollow = true;
                //EndDestination($"A target ship is within our range");
                SetCurrentSpeed(TargetEnemyShipToFollow.CurrentSpeed);
                if (Level.FixedUpdates % 10 == 0 && DistanceTo(TargetEnemyShipToFollow) < HalfMaxRange)
                {
                    EndDestination($"A target ship is within our range");
                }
                return;
            }
            IsCloseEnoughToTargetEnemyShipToFollow = false;
            if (Squad.IsMatchingSpeed)
            {
                SetCurrentSpeed(Squad.CurrentSpeed);
            }
            else
            {
                SetCurrentSpeed(Speed);
            }


        }
        /// <summary>
        /// Either stops the ship or sets it on course to the next destination
        /// </summary>
        /// <param name="reason"></param>
        private void EndDestination(string reason)
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
        private bool IsCloseEnoughToTargetCoordinates(float distance)
        {
            return distance < ConfigData.ShipTurningRadius && !(!IsFollowingPath && HasTargetEnemyShipToFollow && HasCommand && Squad.Command.Type == "Bombing Run" && ProximityCollider.NearbyEnemyShips.Contains(TargetEnemyShipToFollow));
        }
        public void StopMoving(string reason)
        {
            if (IsMobile)
            {
                __LastStopReason = $"{Name} stopped at {GetPosition()} on the way to {TargetCoordinates} because of {reason} at {Age} ticks.";
                //Debug.Log(__LastStopReason);
                SetTargetCoordinates(Vector2.zero);
                Body.velocity = Vector2.zero;
                HasTargetCoordinates = false;
                HasTargetDirection = false;
                TargetDirection = 0;
                if (IsFollowingPath)
                {
                    IsFollowingPath = false;
                    DestinationQueue.Clear();
                    CancelInvoke(nameof(CheckForDirectPath));
                }

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

                //transform.position = TargetCoordinates;
                //SetToDefaultAngle();
            }

        }
        public void SetToDefaultAngle()
        {
            if (Side == ConfigData.Configuration.AISide)
            {
                transform.eulerAngles = Vector3.forward * 180;
            }
        }
        public void Clicked(int mouseButton)
        {
            GameState state = Level.GetState();
            if (!IsUserControlled && mouseButton == LevelInputManager.RightClick) // when this ship has been right clicked on and this ship *is not* user controlled
            {
                //Debug.Log($"Targeted squad #{Squad.SquadNumber}");
                state.GetSelectedSquads().ForEach((selectedSquad) =>
                {
                    //selectedSquad.UserTargetSquad(squad);

                    selectedSquad.UserAggressive(Squad);
                });
            }
            else if (IsUserControlled && mouseButton == LevelInputManager.LeftClick) // when this ship has been left clicked on and this ship *is* user controlled
            {
                state.SelectSquad(Squad);
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
            if (!Level.IsPaused)
            {
                InCombat = false;
                CancelInvoke(nameof(CombatTimer));
                _combatTimer = false;
            }
        }
        /// <summary>
        /// Sets the combat timer. A ship is in combat if it has fired before the combat timer has expired. The timer is currently set to 5 seconds.
        /// In Combat is used for Hivemind Matchup Strategies.
        /// </summary>
        public void SetCombatTimer()
        {
            // if the combat timer already exists, clear it
            if (IsUserControlled && Level.ActivateHiveMind) // The combat timer and In Combat are only used for Hivemind Strategies so it only makes sense to use this when those are in use
            {
                if (_combatTimer)
                {
                    CancelInvoke(nameof(CombatTimer));
                }

                // set the ship as in combat because it is firing
                InCombat = true;

                /* set a timer to check every 5 seconds and if the game is not paused, the ship will be out of combat
                But if the ship fires again within those 5 seconds the above code will clear the timer
                 */
                _combatTimer = true;
                float maxRateOfFire = HasWeapons ? Weapons.Max((w) => w.RateOfFire) : 2;
                float repeatRate = Mathf.Clamp(5f, maxRateOfFire + 1, maxRateOfFire + 2);
                InvokeRepeating(nameof(CombatTimer), repeatRate, repeatRate);
            }

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.name == ("Selection Box"))
            {
                //Debug.Log("Hit selection box");
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
            //else if (collidingThing.CompareTag("Obstacle") && BumperCollider.IsTouching(collider))
            //{
            //    Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
            //    Debug.Log($"{Name} bumper collided with {obstacle.Name}");
            //    StopMoving("Hit obstacle");
            //}
        }
        /// <summary>
        /// Notes the change in health and TSV and updates the health bar for any kind of non-attacking damage the ship takes. See LogAttackingDamage() for attacking damage
        /// </summary>
        public void LogDamage(int damage)  // [damage-method] [note]
        {
            int oldTsv = Tsv;
            Health -= math.min(damage, Health);


            int tsvChange = Tsv - oldTsv;
            FleetShip.DamageReceived += -tsvChange;
            Squad.SavedSquad.Stats.DamageReceived += -tsvChange;

            if (Squad.HasCommand)
            {
                Squad.Command.Tsv += tsvChange; // subtract the TSV from the target
            }
            if (Health == 0)
            {
                Kill(null);
            }
            else
            {
                UpdateHealthBar();
            }
        }
        /// <summary>
        /// Logs damage to a ship from being attacked by another ship. See LogDamage() for non-attacking damage
        /// </summary>
        /// <param name="power"></param>
        /// <param name="shooter"></param>
        /// <param name="target"></param>
        public static void LogAttackingDamage(int power, Ship shooter, Ship target) // [damage-method] [note]
        {
            if (shooter.Level.MakeShotsHarmless)
            {
                power = 0;
            }

            int targetOldTSV = target.Tsv;

            target.Health -= math.min(power, target.Health);


            int targetTSVChange = target.Tsv - targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            LogHitStats(shooter, shooter.Squad, target, target.Squad,targetTSVChange);


            ShipDamageStatus status = shooter.Squad.GetShipDamageStatus(target);
            if (target.Health == 0)
            {
                target.Kill(shooter);
                shooter.Squad.DamageSentToEnemyShipsBySquad.Remove(status);
            }
            else
            {
                target.RLHealth = target.Health / target.MaxHealth;
                target.UpdateHealthBar();

                status.Health = target.Health;
            }

            
        }
        /// <summary>
        /// Logs the stats to the fleet ships, saved squads, and commands of the shooter and the target 
        /// </summary>
        /// <param name="shooter"></param>
        /// <param name="shooterSquad"></param>
        /// <param name="target"></param>
        /// <param name="targetSquad"></param>
        /// <param name="tsvChange"></param>
        /// <param name="isFireShipSelfHit"></param>
        protected static void LogHitStats(Ship shooter, Squad shooterSquad, Ship target, Squad targetSquad, int tsvChange, bool isFireShipSelfHit = false) // [stats-method] [note]
        {
            if (shooter != null)
            {
                shooter.FleetShip.DamageDone += -tsvChange;
                shooter.Squad.SavedSquad.Stats.DamageDone += -tsvChange;

                if (shooterSquad.HasCommand)
                {
                    shooterSquad.Command.Tsv += -tsvChange; // add the TSV (it's negative) to the shooter
                }
            }
            else if (shooterSquad != null)
            {
                //Debug.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null. The shooter squad got stats though.");
                shooterSquad.SavedSquad.Stats.DamageDone += -tsvChange;

            }
            else
            {
                //Debug.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null and the shooterSquad is null. " +
                //    $"Was it a fireship explosion hitting itself? {isFireShipSelfHit}");
            }
            if (target != null)
            {
                target.FleetShip.DamageReceived += -tsvChange;
                target.Squad.SavedSquad.Stats.DamageReceived += -tsvChange;

                if (targetSquad.HasCommand)
                {
                    targetSquad.Command.Tsv += tsvChange; // subtract the TSV (it's negative) from the target
                }

                if (target.Level.IsTrainingNueralNetwork)
                {
                    int[] initialTsv = target.Level.GetState().InitialTsv;
                    //Debug.Log($"Initial TSV: {initialTsv[0]}, {initialTsv[1]}");
                    float percentageTsvDestroyed = (float)Math.Round(((-1.0f * tsvChange) / initialTsv[target.Side - 1]), 3);
                    //Debug.Log($"{shooter.Name} destroyed {percentageTsvDestroyed}  {tsvChange} / {initialTsv[target.Side - 1]} of the total initial tsv of the enemy");
                    target.Brain.AddReward(-percentageTsvDestroyed);

                    if (shooter != null)
                    {
                        shooter.Brain.AddReward(percentageTsvDestroyed);
                    }
                }
                
            }
            else if (targetSquad != null)
            {
                Debugger.Exception($"There was {tsvChange} damage done by {shooter.Name} but the target is null. The target squad got stats though.");
                targetSquad.SavedSquad.Stats.DamageReceived += -1 * tsvChange;
            }
            else
            {
                Debugger.Exception($"There was {tsvChange} damage done by {shooter.Name} but the target is null and the targetSquad is null. ");
            }


        }
        protected void LogKillerStats(Ship killer) // [stats-method] [note]
        {
            killer.FleetShip.Kills++;
            killer.Squad.SavedSquad.Stats.Kills++;
        }
        protected void LogKilledStats() // [stats-method]
        {
            if (Level.ReplaceDeadShips && !IsCarrierShip && !IsMinionShip && Squad.SavedSquad.HasBeenSavedToStorage)
            {
                FleetShip.IsDead = true;
            }
            Squad.SavedSquad.Stats.ShipsLost++;
            FleetShip.MineralsMinedThisLevel = 0;
        }
        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;

            if (collidingThing.name == ("Selection Box") && IsUserControlled)
            {
               Level.Selector.DeselectShip(this);
            }
        }
        public virtual void Kill(Ship killer, bool endKill = false) // [kill method] [stats-method] [note]
        {
            if (!IsDead)
            {
                IsDead = true;
                //Debug.Log($"Killing ship {Name} with size Factor {ConfigData.GetShipSizeFactor(ShipType)}");
                GameState state = Level.GetState();
                if (IsPathfinding)
                {
                    Debug.Log($"{Name} got killed while pathfinding on #{PathfindingThread}");
                }
                if (!endKill)
                {
                    DropExplosionAnimation();

                    if (killer != null)
                    {
                        killer.LastKilled = Time.frameCount;
                        killer.IsCloseEnoughToTargetEnemyShipToFollow = false;
                        LogKillerStats(killer);
                    }
                    LogKilledStats();

                    if (HasVision)
                    {
                        Vision.Kill(0);
                    }
                }


                state.RemoveShip(this);
                Squad.RemoveShip(this);


                // If this is a carrier, get all strikers that belonged to this carrier and mark the last spot the carrier was at
                if (this is Carrier)
                {
                    Carrier nextCarrier = (Carrier)state.GetHumanShips().FirstOrDefault((s) => s is Carrier);
                    if (nextCarrier != null)
                    {
                        state.GetHumanShips().Where((ship) => ship.ShipType == "Striker" && ((Striker)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Striker)ship).Carrier = nextCarrier);
                        state.GetHumanShips().Where((ship) => ship.ShipType == "Drone" && ((Drone)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Drone)ship).Carrier = nextCarrier);
                    }
                    else
                    {
                        Squad.GetShips().Where((ship) => ship.ShipType == "Striker").ToList().ForEach((ship) => {
                            Striker striker = (Striker)ship;
                            striker.LastCarrierPosition = GetPosition();
                        });
                    }

                }

                if (Squad.GetShips().Count == 0)
                {
                    Squad.Kill(endKill);
                }
                else
                {
                    Squad.SetOffsets();
                }
                Destroy(gameObject);
            }
        }
        /// <summary>
        /// Returns the target enemy ship to follow for this ship. The Target enemy ship will be the first in the targeting queue for this ship's squad's command. This is different from which ship its weapons are targeting
        /// </summary>
        /// <returns></returns>
        public Ship SetAndGetTargetEnemy()
        {
            int loop = 0;
            while (!HasTargetEnemyShipToFollow && loop < 10) // [note] the loop check should be removed if no longer needed
            {
                loop++;
                //try
                //{
                //    if (Squad.Command.TargetingQueue.Count == 0)
                //    {
                //        if (Squad.Command.EnemySquad.IsGrowingSquad)
                //        {
                //            Squad.Command.OriginalQueue = new Queue<Ship>(Squad.Command.MakeTargetingQueue());
                //        }
                //        Squad.Command.TargetingQueue = new Queue<Ship>(Squad.Command.OriginalQueue);
                //    }
                //    TargetEnemyShipToFollow = Squad.Command.TargetingQueue.Dequeue();
                //}catch(Exception e)
                //{
                //    Debug.Log($"Squad: {Squad}");
                //    Debug.Log($"Command: {Squad?.Command}"); // command is null
                //    Debug.Log($"TargetingQueue: {Squad?.Command?.TargetingQueue}");
                //    Debug.Log($"Enemy: {Squad?.Command?.EnemySquad?.Name}");
                //    Debug.Log($"Make Targeting Queue: {Squad?.Command?.MakeTargetingQueue()}");
                //    throw e;
                //}

                if (Squad.Command.TargetingQueue.Count == 0)
                {
                    if (Squad.Command.EnemySquad.IsGrowingSquad)
                    {
                        Squad.Command.OriginalQueue = new Queue<Ship>(Squad.Command.MakeTargetingQueue());
                    }
                    Squad.Command.TargetingQueue = new Queue<Ship>(Squad.Command.OriginalQueue);
                }
                TargetEnemyShipToFollow = Squad.Command.TargetingQueue.Dequeue();


                //Debug.Log($"{Name} doesn't have target ships so it's moving towards the target ship in the squad, {TargetEnemy.Name}");
            }
            if (loop == 10)
            {
                Debug.Log($"Squad: {Squad}");
                Debug.Log($"Command: {Squad?.Command}"); 
                Debug.Log($"TargetingQueue: {Squad?.Command?.TargetingQueue}");
                Debug.Log($"Enemy: {Squad?.Command?.EnemySquad?.Name}");
                //Debug.Log($"Make Targeting Queue: {Squad?.Command?.MakeTargetingQueue()}");
                Debug.Log($"Hit loop limit for getTargetEnemy()");
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
            return Weapons.Any((w) => w.IsShipWithinRange(ship));
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
        public bool IsAnySquadShipWithinRange(Squad squad)
        {
            return squad.GetShips().Any((ship) => IsShipWithinRange(ship));
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
        public Vector2 GetRandomPointOnShip(Vector2 nearPosition)
        {
            Vector2 randomPointBounds;
            Vector2 basePosition = GetPosition() + Level.GetPosition();
            float halfWidth = GetHalfWidth() - ConfigData.OffsetFromFront;
            float halfHeight = GetHalfHeight() - ConfigData.OffsetFromFront;

            randomPointBounds = Utilities.ForceBounds(10, 10, halfWidth, halfHeight, -1 * halfWidth, -1 * halfHeight);
            basePosition = nearPosition + Level.GetPosition();

            Vector2 randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, randomPointBounds, Vector2.zero) + basePosition;
            if (!Collider.OverlapPoint(randomPoint))
            {
                return Collider.ClosestPoint(randomPoint);
            }
            return randomPoint;
        }
        public int GetClearance()
        {
            int clearance = Clearance; 
            if (clearance == 0)
            {
                Level.CalculateShipClearances();
                clearance = Level.ShipClearances.GetValueOrDefault(ShipType);
            }
            return clearance;
        }
        /// <summary>
        /// Whether or not the ship is in the bounds of the map
        /// </summary>
        /// <returns></returns>
        public bool IsInBounds()
        {
            return GetPosition() == Level.ForceBounds(GetPosition());
        }


        // Utility methods
        public new string ToString()
        {
            return Name;
        }
        // Uses a list of ships, not necessarily squad ships
        public static double GetAverageHealthPercent(List<Ship> ships)
        {
            double squadTotalHealthPercent = 0;
            foreach(Ship ship in ships)
            {
                double shipHealthPercent = ship.Health / ship.OriginalHealth;
                squadTotalHealthPercent += shipHealthPercent;
            }
            return Math.Round((squadTotalHealthPercent / ships.Count) * 100);
        }


        // UI Methods
        public void ShowWeaponRanges()
        {
            Weapons.ForEach((weapon) =>
            {
                weapon.ShowRange();
            });
        }
        public void HideWeaponRanges()
        {
            Weapons.ForEach((weapon) =>
            {
                weapon.HideRange();
            });
        }
        public void UpdateHealthBar()
        {
            float healthPercent = (float)Math.Round((double)((double)Health / MaxHealth), 2);
            //Debug.Log($"{Name} health: {healthPercent}% MaxHealth: {MaxHealth}");
            _healthBarFiller.localScale = new Vector2(healthPercent, _healthBarFiller.localScale.y);
            //_healthBarFiller.sizeDelta = new Vector2(healthPercent, _healthBarFiller.sizeDelta.y);

            if (healthPercent > .25f && healthPercent <= .50f)
            {
                _healthBarFillerSprite.color = ConfigData.GetUIColor("medium");
            }
            else if (healthPercent <= .25f)
            {
                _healthBarFillerSprite.color = ConfigData.GetUIColor("bad");
            }
        }
        protected void DropExplosionAnimation()
        {
            if (!Level.IsTraining)
            {
                GameObject explosion = LevelStage.Instantiate(ShipExplosion, Vector2.zero, Quaternion.identity);
                explosion.transform.localScale *= ConfigData.GetShipSizeFactor(ShipType);
                explosion.transform.parent = Level.Map.transform;
                explosion.transform.localPosition = GetPosition();
            }
        }
        public void ShowShipStats()
        {
            Debug.Log($"Showing ship stats for {Name}");
        }
        private void OnMouseEnter()
        {
            Debug.Log($"Mouse is over {Name}");
            Invoke(nameof(ShowShipStats), 2);
        }

        private void OnMouseExit()
        {
            Debug.Log($"Mouse has left {Name}");
            CancelInvoke(nameof(ShowShipStats));
        }


    }

}

