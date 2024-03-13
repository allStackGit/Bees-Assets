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

namespace Assets.Scripts.Entities.Ships
{
    public class Ship : Entity
    {
        public int Health, MaxHealth, OriginalHealth, OriginalTsv, Sight, AdditionalTsv;
        public float ProjectileValue, Speed, SpecialFirePower;
        public GameObject ShipExplosion, HealthBar, MiniMapIcon;
        public Vector2 TargetCoordinates, OffsetFromCenter; // the coordinates of where the ship should go, and it's offset from the center of the squad
        public Squad Squad;
        public float DefaultAngle;
        public long LastKilled;
        public FleetShip FleetShip = null;
        public string ShipType, Name;
        public bool FireAtFrontOfShip, InCombat;
        /// <summary>
        /// A ship can be killed at some point of the frame and still exist until the end of the frame. Check this to see if a ship is dead but not yet destroyed.
        /// </summary>
        public bool IsDead;
        public bool HasBrain, IsMinionShip, HasTargetCoordinates;
        public List<Weapon> Weapons;
        public List<GameObject> ProjectilePrefabs, WeaponPrefabs, ColoredPrefabs;
        public Brain Brain = null;
        public Queue<Vector2> DestinationQueue = new Queue<Vector2>();




        // [tsv-calculation] [note]
        public float Firepower => HasWeapons ? Weapons.Sum(w => w.Firepower) : SpecialFirePower;
        public List<Turret> Turrets => Weapons.Where((w) => w is Turret).ToList().ConvertAll<Turret>((w) => (Turret)w);
        public float DamagePerSecond => Turrets.Sum(t => t.DamagePerSecond);
        public int Range => HasWeapons ? Weapons.Max((w) => w.Range) : 0;
        public int Tsv => Utilities.CalculateTsv(this);
        public string ShipTypeLetter => Utilities.ConvertShipNameToType(ShipType);
        public double Seconds => GetLifeTime();
        public float RotationSpeed => Speed * ConfigData.Configuration.RotationMultiplier;
        public bool HasWeapons => Weapons.Count > 0;
        public bool HasTargetShips => TargetShips.Count > 0;
        public bool IsUserControlled => Side == ConfigData.Configuration.UserSide && Level.HasPlayer;
        public bool IsHiveMindControlled => Side == ConfigData.Configuration.AISide || (Side == ConfigData.Configuration.UserSide && !Level.HasPlayer);
        public bool HasReachedDestination => !HasTargetCoordinates;
        public bool IsMoving => Body.velocity != Vector2.zero;
        public bool IsCarrierShip => ShipType == "Striker" || ShipType == "Drone";
        public string ShootingStrategy => HasBrain ? RLShootingStrategy : Squad.GetShootingStrategy();
        public List<Ship> TargetShips => HasWeapons ? Weapons.Select((w) => w.TargetShip).Where((s) => s != null).ToList() : new List<Ship>();
        public bool HasCommand => Squad.HasCommand;


        protected bool aimedAtTarget;


        private bool _combatTimer;
        private float _currentSpeed;
        private Transform _healthBarFiller;
        private SpriteRenderer _healthBarFillerSprite;
        private Vector2 _size;


        // Test variables
        public List<string> __PastCommands = new List<string>();
        public string __Strategy, __Squad, __SquadStatus, __CommandStatus, __LastStopReason;
        public Vector2 __CommandDestination, __Velocity, __TargetCoordinates;
        public float __Firepower, __DamagePerSecond;
        public long __Tsv, __CommandTsv;
        public List<Ship> __TargetShips;
        public List<Ship> __SquadShips;
        public bool __HasReachedDestination;
        public bool __SquadHasReachedDestination;
        public string __Enemy;



        // Neural network
        public int Direction;
        public bool ShouldDetonate;
        public string RLShootingStrategy;
        public float RLSide;
        public float RLHealth;
        public float RLShipType;

        private void UpdateTestProperties()
        {
            __Strategy = Squad.HasCommand && Squad.Command.HasStrategy ? Squad.Command.Strategy.Name : "-";
            __Enemy =  Squad.HasEnemy ? Squad.Command.Enemy.Name : "-";
            __TargetShips = TargetShips;
            __Squad = Squad.Name;
            __SquadStatus = Squad.Status;
            //__CommandStatus = Squad.HasCommand ? Squad.Comd.Status : "-";
            __CommandDestination = Squad.HasCommand ? Squad.Command.GetDestination() : Vector2.zero;
            __TargetCoordinates = TargetCoordinates;
            __Velocity = Body.velocity;
            __Firepower = Firepower;
            __Tsv = Tsv;
            __DamagePerSecond = DamagePerSecond;
            __CommandTsv = Squad.HasCommand ? Squad.Command.Tsv : 0;
            __PastCommands = Squad.PastCommands.Select((c) => $"Command #{c.OutcomeId} - {c.Strategy.Name} against {c.Enemy} ended with {c.Tsv}" +
            $" TSV due to \"{c.FinalizationCause}\" and took {c.Age} ticks").ToList();

            __HasReachedDestination = HasReachedDestination;
            __SquadHasReachedDestination = Squad.HasReachedDestination;
            __SquadShips = Squad.GetShips();

            //AverageReward = AverageRewardSum / Actions;
            //AverageRandomReward = AverageRandomRewardSum / RandomActions;
            //AverageLearnedReward = AverageLearnedRewardSum / LearnedActions;
            //for (int i = 0; i < AverageDirectionReward.Length; i++)
            //{
            //    AverageDirectionReward[i] = AverageDirectionSum[i] / DirectionActionCount[i];
            //}
        }


        // setup methods
        public virtual void Setup(LevelStage level, long id, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
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

            ShipStatBlock shipStats = ConfigData.GetShipInfo(fleetShip.Type);
            Health = shipStats.Health;
            OriginalHealth = Health;

            Name = $"{ShipType} - #{Id}";
            gameObject.name = Name;
            _healthBarFiller = HealthBar.transform.GetChild(0);
            _healthBarFillerSprite = HealthBar.transform.GetChild(0).GetComponent<SpriteRenderer>();

            //Vector2 sizeFactor = (ConfigData.ShipSizes.GetValueOrDefault(ShipType) / ConfigData.Tiny) * 2.22f;


            //HealthBar.transform.localScale = new Vector2(sizeFactor.x, HealthBar.transform.localScale.y);
            //HealthBar.transform.position = new Vector2(sizeFactor.x * -.5f, (sizeFactor.y * -.75f)-.75f);


            //MiniMapIcon.transform.localScale = sizeFactor * 1.5f;
            if (squad.Color != ConfigData.UnsetColor)
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

            if (fleetShip.Type == "Striker")
            {
                SpecialFirePower = shipStats.Powers[0] / 5;
            }
            else if (fleetShip.Type == "Fire Ship")
            {
                SpecialFirePower = shipStats.Powers[0] * shipStats.ProjectileValues[0];
            }
            else if (fleetShip.Type == "Yellow Jacket")
            {
                SpecialFirePower = shipStats.Powers[0] / 5;
            }

            for (int i = 0; i < shipStats.ProjectileValues.Count; i++)
            {
                string weaponType = shipStats.WeaponTypes[i];
                Weapon weapon = null;
                if (weaponType == "Turret")
                {
                    Turret turret = gameObject.AddComponent<Turret>();
                    weapon = turret;
                }
                else if (weaponType == "Eye")
                {
                    Eye eye = gameObject.AddComponent<Eye>();
                    weapon = eye;
                }
                else if (weaponType == "Dual Cannon")
                {
                    DualCannon dualCannon = gameObject.AddComponent<DualCannon>();
                    weapon = dualCannon;
                }
                else if (weaponType == "Beam Cannon")
                {
                    BeamCannon beamCannon = gameObject.AddComponent<BeamCannon>();
                    weapon = beamCannon;
                }
                else if (weaponType == "Bomb")
                {
                    Bomb bomb = gameObject.AddComponent<Bomb>();
                    weapon = bomb;
                }
                else if (weaponType == "Split Shot")
                {
                    LaserBuilder laserBuilder = gameObject.AddComponent<LaserBuilder>();
                    weapon = laserBuilder;
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
                        ((Eye)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }else if (weapon is LaserBuilder)
                    {
                        ((LaserBuilder)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }
                    else
                    {
                        ((Turret)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip, shipStats.RotationRates[i]);
                    }

                }
                else
                {
                    //Debug.Log($"{weapon.GetType()} -- {typeof(Turret)}");
                    weapon.Setup(this, shipStats.Ranges[i], shipStats.Powers[i], SpecialFirePower, shipStats.RatesOfFire[i],
                    shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i]);
                }

                Weapons.Add(weapon);
            }

            AdditionalTsv = shipStats.AdditionalTsv;
            Sight = shipStats.Sight;
            Speed = shipStats.Speed;


            

            OriginalTsv = Utilities.CalculateMaxTsv(this);
            _size = gameObject.GetComponent<SpriteRenderer>().bounds.size;
            //squad.AddShip(this);
            Level.GetState().AddShip(this);
            SetToDefaultAngle();
            SetCurrentSpeed(Speed);


        }
        protected void FixedUpdate()
        {
            if (!Level.IsPaused)
            {
                Move();
                if (!Level.IsTrainingNueralNetwork && !Level.IsTrainingHiveMind)
                {
                    if (Side == ConfigData.Configuration.HumanSide && Level.HasPlayer && !Level.HasFoundAllBees && Level.Audio != null)
                    {
                        CheckForBees();
                    }

                    //if (ConfigData.Development && !IsDead) // [alert] [debug] remove this for release
                    //{
                        //UpdateTestProperties();
                    //}
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
        public void SetColor()
        {
            // set the color
            if (Squad.Color != ConfigData.UnsetColor)
            {
                //Debug.Log("Setting sprite for ship");
                ColoredPrefabs.Add(gameObject);
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                ColoredPrefabs.ForEach((prefab) =>
                {
                    Sprite shipIcon = prefab.GetComponent<SpriteRenderer>().sprite;
                    int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, shipIcon);
                    prefab.GetComponent<SpriteRenderer>().sprite = Utilities.SetImageColor(Squad.Color, shipIcon, changeablePixels);
                });
               
            }
        }
        public void SetSquadName()
        {
            // Set the name of the ships with the Squad name
            Name = $"{Squad.Name}: {Name}";
            gameObject.name = Name;
        }


        // movement methods
        public void MoveToPoint(Vector2 destination)
        {
            destination = Level.ForceBounds(destination);
            if (Level.HasObstacles)
            {
                FindShortestPath(destination);
                if (DestinationQueue.Count > 0)
                {
                    TargetCoordinates = DestinationQueue.Dequeue();
                    HasTargetCoordinates = true;
                    Debug.Log($"We've got a destination queue {DestinationQueue.Count} entries long! Heading to {TargetCoordinates} first");
                }
            }
            else
            {
                TargetCoordinates = destination;
                HasTargetCoordinates = true;
            }
        }

        private void FindShortestPath(Vector2 destination)
        {
            float start = Time.realtimeSinceStartup;
            DestinationQueue.Clear();
            Vector2 startPosition = GetPosition();
            

            //Debug.Log($"Finding shortest path from {startPosition} to {destination} for {Name}");

            if (Utilities.HasObstaclesInTheWay(startPosition, destination))
            {
                //Vector2Int testStart = new Vector2Int(-Level.HalfMapWidth, Level.HalfMapHeight);
                Vector2Int convertedStart = Level.ConvertToPathfindingMapCoordinates(startPosition);
                Vector2Int convertedDestination = Level.ConvertToPathfindingMapCoordinates(destination);
                //Debug.Log($"There is an obstacle in the way, using astar to find a path, start: {convertedStart}, end: {convertedDestination}");
                //Debug.Log(Level.ConvertPathfindingToMapCoordinates(new Vector2Int(0, 0)));
                List<Vector2Int> result = new Astar(Level.PathfindingMap, convertedStart, convertedDestination, Astar.Type.EuclideanFree).Result;
                //Debug.Log(result.Count);
                for (int i = 0; i < result.Count; i++)
                {
                    Vector2 point = Level.ConvertPathfindingMapToLevelCoordinates(result[i]);
                    //Debug.Log($"Destination #{(i + 1)}: {point}");
                    DestinationQueue.Enqueue(point);
                }

            }
            else
            {
                //Debug.Log("There is straight line to the destination");
                DestinationQueue.Enqueue(destination);
            }
            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"FindShortestPath() took {end} ms to complete.");

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

            Vector2 pos = GetPosition();
            transform.localPosition = new Vector2(Mathf.Clamp(pos.x, Level.MinX, Level.MaxX), Mathf.Clamp(pos.y, Level.MinY, Level.MaxY));
            Body.velocity = velocity;

        }
        private void MoveToTargetCoordinates()
        {

            float distance = DistanceToPoint(TargetCoordinates);
            float maxSpeed = (float)_currentSpeed;


            //if (distance > maxSpeed/4)
            //{

            //}

            // Set the velocity of the ship
            float rotation = GetDegreesTowardsPoint(TargetCoordinates);

            Utilities.TimedRotation(gameObject, rotation, RotationSpeed);
            float degrees = transform.eulerAngles.z - 180;
            float angle = degrees * Mathf.Deg2Rad;

            Vector2 velocity = new Vector2((maxSpeed * Mathf.Sin(angle)), (-1 * maxSpeed * Mathf.Cos(angle)));


            if (Squad.IsRetreating)
            {
                velocity *= 1.5f;
            }

            Body.velocity = velocity;

            // stop if you're close enough to your destination

            if (IsCloseEnoughToTargetCoordinates(distance))
            {
                //Debug.Log($"Ship {Name} is close enough ({distance}) to the target coordinates {TargetCoordinates} and will now stop moving.");
                EndDestination($"Ship {Name} is close enough ({distance}) to the target coordinates {TargetCoordinates}");
            }

            //if any of the target ship(s) if your weapons are not dead and are within range
            else if (Squad.IsAttacking && HasTargetShips && !(Squad.HasCommand && (Squad.Command.Type == "Circle" || Squad.Command.Type == "Right Swipe" ||  Squad.Command.Type == "Left Swipe") ||
                Squad.Command.Type == "In and Out") && TargetShips.Any((ship) => ship != null && IsShipWithinRange(ship)))
            {
                //Debug.Log("We are outside of range of the target ship and we can still hit it but we are close to being within its range");
                if (!(Squad.IsAttacking && Squad.Command.Enemy.IsMoving))
                {

                    EndDestination("We are outside of range of the target ship and we can still hit it but we are close to being within its range");
                }
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
                TargetCoordinates = DestinationQueue.Dequeue();
                //Debug.Log($"There are more target coordinates, not ending movement: {TargetCoordinates}");
            }
            else
            {
                StopMoving(reason);
            }
        }
        /// <summary>
        /// Checks if a ship is close enough to its target coordinates
        // [note] if GetHeight() is used then the ships don't endlessly circle but the larger ships stop noticably before their destination and it's hard to move them precisely
        // If CloseEnoughCoordinateVariance is used, the ships move close to the destination but they tend to endlessly circle if they are moved to a nearby destination inside of their
        // turning radius
        /// </summary>
        /// <param name="distance"></param>
        /// <returns></returns>
        private bool IsCloseEnoughToTargetCoordinates(float distance)
        {
            return (distance < Mathf.Clamp(GetHeight(), 0, 5) && !Squad.HasEnemy) || (distance < ConfigData.CloseEnoughCoordinateVariance && !(Squad.HasCommand && Squad.Command.Type == "Bombing Run"));
        }
        public void StopMoving(string reason)
        {
           
            //__LastStopReason = $"Stopped at {GetPosition()} on the way to {TargetCoordinates} because of {reason} at {Age} ticks.";
            //Debug.Log(__LastStopReason);
            TargetCoordinates = Vector2.zero;
            Body.velocity = Vector2.zero;
            HasTargetCoordinates = false;
            //transform.position = TargetCoordinates;
            //SetToDefaultAngle();
        }
        public void SetToDefaultAngle()
        {
            if (Side == ConfigData.Configuration.AISide)
            {
                transform.eulerAngles = Vector3.forward * 180;
            }
            //if (DefaultAngle == 0)
            //{
            //    transform.eulerAngles = Vector3.forward;
            //}
            //else
            //{
            //    //transform.eulerAngles = Vector3.forward * 180;

            //    transform.eulerAngles = Vector3.forward;
            //}
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
        public void SetCurrentSpeed(float speed)
        {
            speed = Mathf.Clamp(speed, 1, Speed);
            _currentSpeed = speed;
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
            //else if (collidingThing.CompareTag("Obstacle"))
            //{
            //    Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
            //    HitObstacle(obstacle);
            //}
        }
        public static void LogDamage(int power, Ship shooter, Ship target) // [damage-method] [note]
        {
            int targetOldTSV = target.Tsv;
            target.Health -= power;

            if (target.Health < 0)
            {
                target.Health = 0;
            }


            int targetTSVChange = target.Tsv - targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            LogHitStats(shooter, shooter.Squad, target, target.Squad,targetTSVChange);

            // each hit, add the negative TSV to the target's command and subtract the negative TSV from the shooter's command

            if (shooter.Squad.Command != null)
            {
                shooter.Squad.Command.Tsv += -1 * targetTSVChange; // add the TSV to the shooter
            }
            if (target.Squad.Command != null)
            {
                target.Squad.Command.Tsv += targetTSVChange; // subtract the TSV from the target
            }

            ShipDamageStatus status = shooter.Squad.GetShipDamageStatus(target);
            if (target.Health <= 0)
            {
                target.Kill(shooter);
                shooter.Squad.DamageSentToEnemyShipsBySquad.Remove(status);
            }
            else
            {
                target.RLHealth = target.Health / target.MaxHealth;
                target.UpdateHealthBar();

                if (status.totalDamageSentToShip > power)
                {
                    status.totalDamageSentToShip -= power;
                }
                status.health = target.Health;
            }

            
        }
        protected static void LogHitStats(Ship shooter, Squad shooterSquad, Ship target, Squad targetSquad, int tsvChange, bool isFireShipSelfHit = false) // [stats-method] [note]
        {
            if (shooter != null)
            {
                shooter.FleetShip.DamageDone += -1 * tsvChange;
                shooter.Squad.SavedSquad.Stats.DamageDone += -1 * tsvChange;
            }
            else if (shooterSquad != null)
            {
                //Debug.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null. The shooter squad got stats though.");
                shooterSquad.SavedSquad.Stats.DamageDone += -1 * tsvChange;
            }
            else
            {
                //Debug.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null and the shooterSquad is null. " +
                //    $"Was it a fireship explosion hitting itself? {isFireShipSelfHit}");
            }
            if (target != null)
            {
                target.FleetShip.DamageReceived += -1 * tsvChange;
                target.Squad.SavedSquad.Stats.DamageReceived += -1 * tsvChange;

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
        protected void LogKillStats(Ship killer) // [stats-method] [note]
        {
            if (Level.ReplaceDeadShips && !IsCarrierShip && !IsMinionShip && Squad.SavedSquad.HasBeenSavedToStorage)
            {
                FleetShip.IsDead = true;
            }
            Squad.SavedSquad.Stats.ShipsLost++;
            killer.FleetShip.Kills++;
            killer.Squad.SavedSquad.Stats.Kills++;
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

                if (!endKill)
                {
                    DropExplosionAnimation();

                    if (killer != null)
                    {
                        killer.LastKilled = Time.frameCount;
                        LogKillStats(killer);
                    }
                    else
                    {
                        if (Level.ReplaceDeadShips && !IsCarrierShip && !IsMinionShip && Squad.SavedSquad.HasBeenSavedToStorage)
                        {
                            FleetShip.IsDead = true;
                        }
                        Squad.SavedSquad.Stats.ShipsLost++;
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
                        state.GetHumanShips().Where((ship) => ship is Striker && ((Striker)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Striker)ship).Carrier = nextCarrier);
                        state.GetHumanShips().Where((ship) => ship is Drone && ((Drone)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Drone)ship).Carrier = nextCarrier);
                    }
                    else
                    {
                        state.GetHumanShips().Where((ship) => ship is Striker && ((Striker)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Striker)ship).LastCarrierPosition = GetPosition());
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


        /* Range and distance methods */
        private float DistanceToClosestShip()
        {
            return DistanceTo(Level.GetState().GetShips().Where((s) => !Equals(s)).OrderBy((s) => DistanceTo(s)).First());
        }
        private float LengthOfLongestSide()
        {
            float width = GetHalfWidth();
            float height = GetHalfHeight();
            return width > height ? width : height;
        }
        public bool IsWithinRangeOfAnyEnemyShips()
        {
            return Level.GetState().GetAllEnemyShips(Side).Any((s) => s.IsShipWithinRange(this));
        }
        public bool IsTooCloseToShip(Ship ship)
        {
            return DistanceTo(ship) <= ConfigData.CloseEnoughCoordinateVariance;
        }
        public bool IsShipWithinRange(Ship ship)
        {
            return Weapons.Any((w) => w.IsShipWithinRange(ship));
        }
        public bool IsAnySquadShipWithinRange(Squad squad)
        {
            return squad.GetShips().Any((ship) => IsShipWithinRange(ship));
        }
        public bool AreAllSquadShipsWithinRange(Squad squad)
        {
            return squad.GetShips().All((ship) => IsShipWithinRange(ship));
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
        public float GetRotation()
        {
            return transform.eulerAngles.z;
        }
        public bool CanSeeShip(Ship ship)
        {
            return DistanceTo(ship) < Sight;
        }
        public Vector2 GetRandomPointOnShip(Vector2 nearPosition)
        {
            Collider2D collider = GetComponent<Collider2D>();
            Vector2 randomPointBounds;
            Vector2 basePosition = GetPosition() + Level.GetPosition();
            float halfWidth = GetHalfWidth() - ConfigData.OffsetFromFront;
            float halfHeight = GetHalfHeight() - ConfigData.OffsetFromFront;

            if (nearPosition != Vector2.zero)
            {
                randomPointBounds = new Vector2(10, 10); // Limit the random point to some point near the nearPosiition
                randomPointBounds = Utilities.ForceBounds(10, 10, halfWidth, halfHeight, -1 * halfWidth, -1 * halfHeight);
                basePosition = nearPosition + Level.GetPosition();
            }
            else
            {
                randomPointBounds = new Vector2(halfWidth, halfHeight);
            }
             
            Vector2 randomPoint = Vector2.zero;
            int loops;
            for (loops = 0; loops < 250 && !collider.OverlapPoint(randomPoint); loops++)
            {
                randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, randomPointBounds, Vector2.zero) + basePosition;
            }
            if (loops >= 250)
            {
                //Debug.Log($"Couldn't find a random coordinate that overlapped the collider for {Name}: {randomPoint}");
            }
            return randomPoint;
        }


        // Utility methods
        public new string ToString()
        {
            return $"Ship Number #{Id} - {FleetShip.Name}";
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
            if (!Level.IsTrainingNueralNetwork && !Level.IsTrainingHiveMind)
            {
                GameObject explosion = LevelStage.Instantiate(ShipExplosion, Vector2.zero, Quaternion.identity);
                explosion.transform.localScale *= ConfigData.GetShipSizeFactor(ShipType);
                explosion.transform.parent = Level.Map.transform;
                explosion.transform.localPosition = GetPosition();
            }
        }





    }

}

