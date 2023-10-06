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
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Server;
using Unity.MLAgents;

namespace Assets.Scripts.Entities.Ships
{
    public class Ship : Entity
    {
        public int Health,  OriginalHealth, OriginalTsv, Sight, AdditionalTsv;
        public float ProjectileValue, Speed, SpecialFirePower;
        public GameObject ShipExplosion, HealthBar, MiniMapIcon;
        public bool InCombat;
        public Vector2 TargetCoordinates, OffsetFromCenter; // the coordinates of where the ship should go, and it's offset from the center of the squad
        public Squad Squad;
        public float DefaultAngle;
        public long LastKilled;
        public FleetShip FleetShip = null;
        public string ShipType;
        public bool FireAtFrontOfShip;
        public List<Weapon> Weapons;
        public List<GameObject> ProjectilePrefabs, WeaponPrefabs;
        public Brain Brain = null;
        public bool HasBrain = false;





        // [tsv-calculation] [note]
        public float Firepower => HasWeapons ? Weapons.Sum(w => w.Firepower) : SpecialFirePower;
        public List<Turret> Turrets => Weapons.Where((w) => w is Turret).ToList().ConvertAll<Turret>((w) => (Turret)w);
        public float DamagePerSecond => Turrets.Sum(t => t.DamagePerSecond);
        public int Range => HasWeapons ? Weapons.Max((w) => w.Range) : 0;
        public int MaxHealth => FleetShip.MaxHealth;
        public int Tsv => Utilities.CalculateTsv(this);
        public string ShipTypeLetter => Utilities.ConvertShipNameToType(ShipType);
        public double Seconds => GetLifeTime();
        public float RotationSpeed => Speed * ConfigData.Configuration.RotationMultiplier;
        public bool HasWeapons => Weapons.Count > 0;
        public bool IsDead => died;
        public bool HasTargetShips => TargetShips.Count > 0;
        public bool IsUserControlled => Side == ConfigData.Configuration.UserSide && Level.HasPlayer;
        public bool IsHiveMindControlled => Side == ConfigData.Configuration.AISide || (Side == ConfigData.Configuration.UserSide && !Level.HasPlayer);
        public bool HasReachedDestination => TargetCoordinates == Vector2.zero && Body.velocity == Vector2.zero;
        public Vector2 Velocity => Body.velocity;
        public bool IsMoving => Body.velocity != Vector2.zero;
        public bool IsCarrierShip => ShipType == "Striker" || ShipType == "Drone";
        public string Name => $"{ShipType} - #{Id}";
        public string ShootingStrategy => HasBrain ? RLShootingStrategy : Squad.GetShootingStrategy();
        public List<Ship> TargetShips => HasWeapons ? Weapons.Select((w) => w.TargetShip).Where((s) => s != null).ToList() : new List<Ship>();
        public bool HasCommand => Squad.HasCommand;


        protected bool aimedAtTarget, died;


        private bool _combatTimer;
        private float _currentSpeed;
        private Transform _healthBarFiller;
        private SpriteRenderer _healthBarFillerSprite;
        private Vector2 _size;


        public List<string> __PastCommands = new List<string>();
        public string __Strategy, __Squad, __SquadStatus, __CommandStatus, __LastStopReason;
        public Vector2 __CommandDestination, __Velocity, __TargetCoordinates;
        public float __Firepower, __DamagePerSecond;
        public long __Tsv, __CommandTsv;
        public List<Ship> __TargetShips;
        public List<Ship> __SquadShips;
        public bool __HasReachedDestination;
        public bool __SquadHasReachedDestination;



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
            //Debugger.Log($"Setting up ship IsCarrierShip: {IsCarrierShip}");

            Id = id;
            Squad = squad;
            Side = squad.Side;
            Level = level;
            FleetShip = fleetShip;
            ShipType = FleetShip.Type;
            OffsetFromCenter = offsetFromCenter;
            Body = GetComponent<Rigidbody2D>();
            Transform brain = transform.Find("Brain");
            if (brain != null && Level.ActivateBrains)
            {
                //Debugger.Log($"Found a brain for {Name}, {brain}");
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


            _healthBarFiller = HealthBar.transform.GetChild(0);
            _healthBarFillerSprite = HealthBar.transform.GetChild(0).GetComponent<SpriteRenderer>();

            Vector2 sizeFactor = (ConfigData.ShipSizes.GetValueOrDefault(ShipType) / ConfigData.Tiny) * 2.22f;
            //HealthBar.transform.localScale = new Vector2(sizeFactor.x, HealthBar.transform.localScale.y);
            //HealthBar.transform.position = new Vector2(sizeFactor.x * -.5f, (sizeFactor.y * -.75f)-.75f);


            MiniMapIcon.transform.localScale = sizeFactor * 1.5f;
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
                    //Debugger.Log($"it's a turret!");
                    if (weapon is Eye)
                    {
                        ((Eye)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip);
                    }else if (weapon is LaserBuilder)
                    {
                        ((LaserBuilder)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip);
                    }
                    else
                    {
                        ((Turret)weapon).Setup(this, shipStats.Ranges[i], shipStats.Powers[i], shipStats.RatesOfFire[i],
shipStats.ProjectileValues[i], WeaponPrefabs[i], ProjectilePrefabs[i], FireAtFrontOfShip);
                    }

                }
                else
                {
                    //Debugger.Log($"{weapon.GetType()} -- {typeof(Turret)}");
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
        public Vector2 RelativeSizeScale()
        {
            if (ShipType != "Gunship" && ShipType != "Hornet") // [alert] this is needed because the gunship is square. There should be a better solution
            {
                return ConfigData.ShipSizes.GetValueOrDefault(ShipType) / ConfigData.Tiny;
            }
            return Vector2.one;
        }
        protected void FixedUpdate()
        {
            if (!Level.IsPaused)
            {
                Move();
                if (!Level.IsTraining)
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
                //Debugger.Log("Setting sprite for ship");
                Sprite shipIcon = gameObject.GetComponent<SpriteRenderer>().sprite;
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType), shipIcon);
                gameObject.GetComponent<SpriteRenderer>().sprite = Utilities.SetImageColor(Squad.Color, shipIcon, changeablePixels);
            }
        }


        // movement methods
        private void Move()
        {
            if (HasBrain && !Squad.IsUserControlled)
            {
                NNDirectionalMovement();
            }
            else
            {
                if (TargetCoordinates != Vector2.zero)
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
            if (TargetCoordinates == Vector2.zero || DistanceToPoint(TargetCoordinates) > GetHeight())
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



            // Set the velocity of the ship
            float maxSpeed = (float)GetCurrentSpeed();
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

            // [note] if GetHeight() is used then the ships don't endlessly circle but the larger ships stop noticably before their destination and it's hard to move them precisely
            // If CloseEnoughCoordinateVariance is used, the ships move close to the destination but they tend to endlessly circle if they are moved to a nearby destination inside of their
            // turning radius
            float distance = DistanceToPoint(TargetCoordinates);
            if ((distance < GetHeight() && !Squad.HasEnemy) || (distance < ConfigData.CloseEnoughCoordinateVariance))
            {
                //Debugger.Log($"Ship {Id} is close enough {DistanceToPoint(TargetCoordinates)} to the target coordinates {TargetCoordinates} and will now stop moving.");
                StopMoving($"Ship #{Id} is close enough ({DistanceToPoint(TargetCoordinates)}) to the target coordinates {TargetCoordinates}");
            }

            //if any of the target ship(s) if your weapons are not dead and are within range and you're not within range of any enemy ships
            else if (Squad.IsAttacking && HasTargetShips && !(Squad.HasCommand && (Squad.Command.Type == "Circle" || Squad.Command.Type == "Right Swipe" ||  Squad.Command.Type == "Left Swipe") ||
                Squad.Command.Type == "In and Out") && TargetShips.Any((ship) => !ship.IsDead && IsShipWithinRange(ship) && DistanceTo(ship)-Range < -5))
            {
                //Debugger.Log("We are outside of range of the target ship and we can still hit it but we are close to being within its range");
                string reason = "We are outside of range of the target ship and we can still hit it but we are close to being within its range";
                if (Squad.IsAttacking && Squad.Command.Enemy.IsMoving)
                {
                    //StopGaining(reason);
                }
                else
                {
                    StopMoving(reason);
                }

            }
            //else if(DistanceToClosestShip() < LengthOfLongestSide())
            //{
            //    StopMoving("Too close to another ship");
            //}


        }
        private void StopMoving(string reason)
        {
           
            __LastStopReason = $"Stopped at {GetPosition()} on the way to {TargetCoordinates} because of {reason} at {Age} ticks.";
            TargetCoordinates = Vector2.zero;
            Body.velocity = Vector2.zero;
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
                //Debugger.Log($"Targeted squad #{Squad.SquadNumber}");
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
        public double GetCurrentSpeed()
        {
            return _currentSpeed;
        }
        public void SetCurrentSpeed(float speed)
        {
            speed = Mathf.Clamp(speed, 1, Speed);
            _currentSpeed = speed;
        }
       

        // Combat methods
        private void CombatTimer()
        {
            if (!Level.IsPaused)
            {
                InCombat = false;
                CancelInvoke(nameof(CombatTimer));
                _combatTimer = false;
            }
        }
        public void SetCombatTimer()
        {
            // if the combat timer already exists, clear it

            if (_combatTimer)
            {
                CancelInvoke(nameof(CombatTimer));
            }

            // set the ship as in combat because it is firing
            InCombat = true;

            /* set a timer to check every 2 seconds and if the game is not paused, the ship will be out of combat
            But if the ship fires again within those two seconds the above code will clear the timer
             */
            _combatTimer = true;
            float maxRateOfFire = HasWeapons ? Weapons.Max((w) => w.RateOfFire) : 2;
            float repeatRate = Mathf.Clamp(2f, maxRateOfFire + 1, maxRateOfFire + 2);
            InvokeRepeating(nameof(CombatTimer), repeatRate, repeatRate);
        }
        protected virtual void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Projectile"))
            {
                ProjectileCollision(collidingThing);
            }else if (collidingThing.name == ("Selection Box"))
            {
                //Debugger.Log("Hit selection box");
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
        }
        protected void ProjectileCollision(GameObject collidingThing)
        {
            Projectile projectile = (Projectile)collidingThing.GetComponent(typeof(Projectile));
            RocketExplosion explosion = (RocketExplosion)collidingThing.GetComponent(typeof(RocketExplosion));
            bool isRocket = projectile is Rocket;

            if (projectile != null)
            {
                Ship shooter = projectile.Shooter;

                // if hit by enemy projectile or fire ship explosion. the ships to ignore is for leafcutter split shots
                if ((!IsFriendly(projectile) || (projectile.Shooter.ShipType == "Fire Ship" && !Equals(shooter))) && !projectile.ShipsToIgnore.Contains(this)) 
                {

                    if (explosion != null)
                    {
                        //Debugger.Log($"{ShipType} #{Id} got hit by {projectile.name}");
                        if (explosion.HasHitShip(this)) // if it's an explosion it should do damage but not if it's already contacted the ship
                        {
                            return;
                        }
                    }

                    projectile.ContactTarget(this);

                    if (!isRocket) // if it's a rocket don't do damage because the explosion is what does damage
                    {
                        LogDamage(projectile.Power, shooter, this);
                    }

                }
            }
        }
        public void UpdateHealthBar()
        {
            float healthPercent = (float)Math.Round((double)((double)Health/MaxHealth), 2);
            //Debugger.Log($"{Name} health: {healthPercent}%");
            _healthBarFiller.localScale = new Vector2(healthPercent, _healthBarFiller.localScale.y);
            //_healthBarFiller.sizeDelta = new Vector2(healthPercent, _healthBarFiller.sizeDelta.y);

            if (healthPercent > .25f && healthPercent <= .50f)
            {
                _healthBarFillerSprite.color = ConfigData.GetUIColor("medium");
            }else if (healthPercent <= .25f)
            {
                _healthBarFillerSprite.color = ConfigData.GetUIColor("bad");
            }
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
        protected static void LogHitStats(Ship shooter, Squad shooterSquad, Ship target, Squad targetSquad, int tsvChange, bool isFireShipSelfHit = false) // [stat-method] [note]
        {
            if (shooter != null)
            {
                shooter.FleetShip.DamageDone += -1 * tsvChange;
                shooter.Squad.SavedSquad.Stats.DamageDone += -1 * tsvChange;
            }
            else if (shooterSquad != null)
            {
                //Debugger.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null. The shooter squad got stats though.");
                shooterSquad.SavedSquad.Stats.DamageDone += -1 * tsvChange;
            }
            else
            {
                //Debugger.Log($"There was {tsvChange} damage done against {target.Name} but the shooter is null and the shooterSquad is null. " +
                //    $"Was it a fireship explosion hitting itself? {isFireShipSelfHit}");
            }
            if (target != null)
            {
                target.FleetShip.DamageReceived += -1 * tsvChange;
                target.Squad.SavedSquad.Stats.DamageReceived += -1 * tsvChange;

                if (target.Level.IsTraining)
                {
                    int[] initialTsv = target.Level.GetState().InitialTsv;
                    //Debugger.Log($"Initial TSV: {initialTsv[0]}, {initialTsv[1]}");
                    float percentageTsvDestroyed = (float)Math.Round(((-1.0f * tsvChange) / initialTsv[target.Side - 1]), 3);
                    //Debugger.Log($"{shooter.Name} destroyed {percentageTsvDestroyed}  {tsvChange} / {initialTsv[target.Side - 1]} of the total initial tsv of the enemy");
                    target.Brain.AddReward(-percentageTsvDestroyed);

                    if (shooter != null)
                    {
                        shooter.Brain.AddReward(percentageTsvDestroyed);
                    }
                }
                
            }
            else if (targetSquad != null)
            {
                //Debugger.Log($"There was {tsvChange} damage done by {shooter.Name} but the target is null. The target squad got stats though.");
                targetSquad.SavedSquad.Stats.DamageReceived += -1 * tsvChange;
            }
            else
            {
                Debugger.Log($"There was {tsvChange} damage done by {shooter.Name} but the target is null and the targetSquad is null. ");
            }


        }
        protected void LogKillStats(Ship killer) // [stats-method] [note]
        {
            if (!IsCarrierShip)
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
                //Debugger.Log($"Killing ship {Name}");
                died = true;
                if (!Level.IsTraining)
                {
                    GameObject explosion = LevelStage.Instantiate(ShipExplosion, Vector2.zero, Quaternion.identity);
                    explosion.transform.localScale *= RelativeSizeScale();
                    explosion.transform.parent = Level.Map.transform;
                    explosion.transform.localPosition = GetPosition();
                }

                GameState state = Level.GetState();
                state.RemoveShip(this);
                Squad.RemoveShip(this);


                // If this is a carrier, get all strikers that belonged to this carrier and mark the last spot the carrier was at
                if (this is Carrier)
                {
                    Carrier nextCarrier = (Carrier) state.GetHumanShips().FirstOrDefault((s) => s is Carrier);
                    if (nextCarrier != null){
                        state.GetHumanShips().Where((ship) => ship is Striker && ((Striker)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Striker)ship).Carrier = nextCarrier);
                        state.GetHumanShips().Where((ship) => ship is Drone && ((Drone)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Drone)ship).Carrier = nextCarrier);
                    }
                    else
                    {
                        state.GetHumanShips().Where((ship) => ship is Striker && ((Striker)ship).Carrier.Equals(this)).ToList().ForEach((ship) => ((Striker)ship).LastCarrierPosition = GetPosition());
                    }
                    
                }

                if (killer != null)
                {
                    killer.LastKilled = state.Ticks;
                    LogKillStats(killer);
                }
                else
                {
                    if (!IsCarrierShip && Squad.SavedSquad.HasBeenSaved)
                    {
                        FleetShip.IsDead = true;
                    }
                    Squad.SavedSquad.Stats.ShipsLost++;
                }

                if (Squad.GetShips().Count <= 0)
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
            return Weapons.Any((w) => w.IsPointWithinRange(ship.GetPosition()));
        }
        public bool IsSquadPositionWithinRange(Squad squad)
        {
            return Weapons.Any((w) =>  w.IsPointWithinRange(squad.GetPosition()));
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
            //Debugger.Log($"{FleetShip.Name} has a sprite width of {gameObject.GetComponent<SpriteRenderer>().bounds.size.x}");
            return _size.x;
        }
        public float GetHalfWidth()
        {
            return GetWidth() / 2;
        }
        public float GetHeight()
        {
            //Debugger.Log($"{FleetShip.Name} has a sprite height of {gameObject.GetComponent<SpriteRenderer>().bounds.size.y}");
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
 



    }

}

