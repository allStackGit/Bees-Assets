

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Turret : Weapon
    {
        public bool ShouldFireAtFrontOfShip, IsAimedAtTarget;
        /// <summary>
        /// Whether or not the ship is being controlled by a user to fire towards a particular point on the map
        /// </summary>
        public bool IsFiringManually, HasTargetingMarker;
        /// <summary>
        /// When the turret is completely ready to fire. It has a target, it's aimed at the target and it's rate of fire has completed
        /// </summary>
        public bool ReadyToFire;
        public int TargetingPasses, PassesPerFire;
        /// <summary> How many times the targeting sequence runs per fire sequence </summary>
        public float TargetingRate;
        public float DamagePerSecond;
        public Vector2 TargetPoint;
        public GameObject TargetingMarker;
        public CollisionAsteroid TargetAsteroid;
        public bool HasTargetAsteroid;
        public bool IsFiringAtAsteroid;
        /// <summary>
        /// Whether or not the ship should fire at the asteroid: If it's not cease fire and it has a target asteroid, and it does not have a target ship
        /// </summary>
        public bool ShouldFireAtAsteroid => !CeaseFire && HasTargetAsteroid && TargetShip == null && !TargetAsteroid.IsDead;

        //public override bool ShouldFire => TargetShip != null && !CeaseFire && IsShipValidTarget(TargetShip);

        public virtual void Create(Ship ship, ConfigData.WeaponTypes type, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType,
            bool fireAtFrontOfShip, float rotationRate)
        {
            base.Create(ship, type, range, power, 0, rateOfFire, projectileValue, piece, projectileType);
            ShouldFireAtFrontOfShip = fireAtFrontOfShip;
            PassesPerFire = 3;
            TargetingRate = RateOfFire / PassesPerFire;
            RotationRate = rotationRate;
            DamagePerSecond = RateOfFire > 0 ? (Power / RateOfFire) : 0;

            if (Ship.IsUserControlled)
            {
                TargetingMarker = Instantiate(Stage.Prefabs.TargetingMarkerPrefab, Vector2.zero, Quaternion.identity);
                TargetingMarker.SetActive(false);
                HasTargetingMarker = true;
            }
        }

        public override void Setup()
        {
            base.Setup();
            if (Ship.IsUserControlled)
            {
                TargetingMarker.transform.SetParent(Level.Map.transform);
                TargetingMarker.name = $"{Name}'s Targeting Marker";
            }
            if (RateOfFire > 0)
            {
                //Debug.Log($"Aiming rate: {TargetingRate} for {FleetShip.Name}");
                InvokeRepeating(nameof(TargetingSequence), TargetingRate, TargetingRate);
                //Invoke(nameof(Fire), RateOfFire);
            }

        }
        public override void ClearData()
        {
            base.ClearData();
            IsAimedAtTarget = false;
            IsFiringManually = false;
            ReadyToFire = false;
            TargetPoint = Vector2.zero;
            TargetAsteroid = null;
            HasTargetAsteroid = false;
            IsFiringAtAsteroid = false;
        }
        protected void MoveTargetingMarker()
        {
            if (HasTargetingMarker && Ship.Squad.IsSelected && IsAimedAtTarget)
            {
                TargetingMarker.transform.position = TargetPoint;
                TargetingMarker.SetActive(true);
            }
            else if (HasTargetingMarker)
            {
                TargetingMarker.SetActive(false);
            }
        }
        private void FixedUpdate()
        {
            Aim();
        }
        /// <summary>
        /// First in the Targeting sequence. On Passes #1 and #2 it runs Targeting. On Pass #3 it runs TryToFire()
        /// </summary>
        private void TargetingSequence()
        {
            __NotShootingReason = $"Reset Reason.";
            TargetingPasses++;
            if ((ReadyToFire && IsAimedAtTarget) || TargetingPasses == PassesPerFire)
            {
                //Debug.Log($"{Name} hit {TargetingPasses} targeting passes, now firing");
                TryToFire();
            }
            else if (!IsFiringManually)
            {
                if (!CeaseFire)
                {
                    Targeting();
                }
                //Debug.Log($"{Name} targeting #{TargetingPasses}");

            }
            else
            {
                SetTargetShipNull();
            }

            if (TargetShip == null)
            {
                TryToFindAsteroidTarget();
            }
            
        }
        public void TryToFindAsteroidTarget()
        {
            if (Ship.NearbyAsteroids.Count > 0)
            {
                TargetAsteroid = Ship.NearbyAsteroids[0];
                HasTargetAsteroid = true;
            }
            else
            {
                HasTargetAsteroid = false;
            }
        }
        protected void SetTargetShipNull()
        {
            TargetShip = null;
        }
        protected override void SetTargetShip(Ship targetShip)
        {
            TargetShip = targetShip;

        }
        /// <summary>
        /// Run on FixedUpdate(). Aims the Turret at the TargetShip, if one exists and can be aimed at
        /// </summary>
        protected virtual void Aim()
        {
            if (IsFiringManually)
            {
                TargetPoint = Stage.InputManager.GetMousePosition();
                IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);
            }
            else
            {
                if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);
                    IsFiringAtAsteroid = false;
                }
                else if (ShouldFireAtAsteroid)
                {
                    TargetPoint = TargetAsteroid.GetPosition();
                    IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);
                    IsFiringAtAsteroid = true;
                }
                else
                {
                    IsAimedAtTarget = false;
                    if (CeaseFire || !HasValidTarget())
                    {
                        //Debug.Log($"{Name} has no ships to fire at, returning to default aim");
                        Utilities.TimedRotation(Piece, Ship.GetRotation(), RotationRate);
                    }
                    IsFiringAtAsteroid = false;
                }
            }
            MoveTargetingMarker();


        }
        /// <summary>
        /// Checks all ships within range to see if this turret can fire upon them
        /// </summary>
        /// <returns></returns>
        public bool HasValidTarget()
        {
            return ShipsWithinRange.Any((targetShip) => !targetShip.IsDead && IsShipValidTarget(targetShip));
        }
        /// <summary>
        /// Checks if the ship is a) within range, b) In the map, and c) Not blocked by obstacles
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead && IsShipWithinRange(potentialTargetShip) && potentialTargetShip.IsInBounds() && (!Level.HasObstacles || !Utilities.HasObstaclesInTheWay(GetPosition(), GetTargetPoint(potentialTargetShip)));
        }
        /// <summary>
        /// Sends the projectile to the Target Ship. Last in the Targeting sequence.
        /// </summary>
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {
            base.SendProjectile();
            //Debug.Log("Sending turret projectile");
            float angle = AngleToPoint(TargetPoint);

            Level.AddProjectile(ProjectileType, this, GetPosition(), angle);
            Ship.FleetShip.ShotsFired++;

        }
        /// <summary>
        /// Gets the target point on a ship that should be fired at
        /// </summary>
        /// <param name="ship"></param>
        /// <returns></returns>
        protected Vector2 GetTargetPoint(Ship ship)
        {
            Vector2 targetPoint = ship.GetPosition();
            if (ShouldFireAtFrontOfShip)
            {
                Vector2 frontOfShip = targetPoint + new Vector2(0, ship.GetHalfHeight() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ship.ShipType));
                //Debug.Log($"{ship} is positioned at {ship.GetPosition()} and target point is {frontOfShip}");
                targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, ship.GetRotation() * Mathf.Deg2Rad);

            }
            if (!RangeCollider.Collider.OverlapPoint(targetPoint+Level.GetPosition()))
            {
                Vector2 colliderPoint = ship.Collider.ClosestPoint(GetPosition());
                if (colliderPoint != GetPosition())
                {
                    targetPoint = colliderPoint;
                }
                //Debug.Log($"{Ship.Name} is firing at {ship.Name} but the target point is not within range. The new target point is: {targetPoint}");

            }
            return targetPoint;
        }
        /// <summary>Immediately fires at the next available target</summary>
        protected void FireNext()
        {
            //Debug.Log($"{Name} could not fire at {TargetShip}, trying to find a new target to fire upon");
            if (CachedTargetingQueue.Count > 0)
            {
                if (!DetermineTargetShip(CachedTargetingQueue, true))
                {
                    DetermineTargetShip(CachedTargetingQueue, false);
                }
                if (ShouldFire)
                {
                    TryToFire();
                }
            }
            else
            {
                //Debug.Log($"{Name} is not firing because there is nothing in the cached targeting queue. Returning to normal sequence");
            }
        }
        /// <summary>
        /// Checks to see if this turret is aimed at any ships in the CachedTargetingQueue and returns the first one
        /// </summary>
        /// <returns></returns>
        protected Ship GetAimedAtTarget()
        {
            foreach (Ship ship in CachedTargetingQueue)
            {
                if (ship != null)
                {
                    if (Utilities.IsAimedAt(Piece, GetDegreesTowardsPoint(GetTargetPoint(ship))))
                    {
                        return ship;
                    }
                }

            }
            return null;
        }
        /// <summary>
        /// Actually fires the projectile and sets the combat timers. Second to last in the targeting sequence.
        /// </summary>
        protected void Fire()
        {
            //Debug.Log($"{Name} is firing at {TargetShip.Name}");
            Ship.SetCombatTimer();

            TargetShip.SetCombatTimer();

            SendProjectile();
            ReadyToFire = false;
        }
        protected void FireAtPoint()
        {
            Ship.SetCombatTimer();
            SendProjectile();

        }
        /// <summary>
        /// Tries to fire if the weapon has a valid target. Second in the targeting sequence but only called on the 3rd pass
        /// </summary>
        protected void TryToFire()
        {
            //Debug.Log($"{Name} trying to fire");
            if (IsFiringManually || IsFiringAtAsteroid)
            {
                if (IsAimedAtTarget)
                {
                    FireAtPoint();
                }
            }
            else
            {
                if (ShouldFire)
                {
                    if (IsAimedAtTarget)
                    {
                        Fire();

                    }
                    else
                    {
                        __NotShootingReason = $"{Ship.Name} is not firing {Name} because the piece is not aimed at the target: {TargetShip.Name}";
                        Ship potentialTargetShip = GetAimedAtTarget();
                        if (potentialTargetShip != null)
                        {
                            SetTargetShip(potentialTargetShip);
                            Fire();
                            //Debug.Log($"{Name} was not aimed at it's target but was aimed at another target: {TargetShip.Name}. Firing");

                        }
                        else
                        {
                            //Debug.Log($"{Ship.Name} is not firing {Name} because the piece is not aimed at any target");
                            __NotShootingReason = $"{Ship.Name} is not firing {Name} because the piece is not aimed at any target";
                            //Invoke(nameof(FireNext), .1f);
                            ReadyToFire = true;
                        }
                    }

                }
                else
                {
                    if (TargetShip == null)
                    {
                        //Debug.Log($"{Ship.Name} is not firing {Name} because the TargetShip is null");
                        __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because the TargetShip is null";
                        FireNext();
                    }
                    else if (CeaseFire)
                    {
                        //Debug.Log($"{Ship.Name} is not firing {Piece.name} because CeaseFire is on");
                        __NotShootingReason = $"{Ship.Name} is not firing {Name} because CeaseFire is on";
                    }
                    //Invoke(nameof(Fire), RateOfFire);
                }
            }

            // Reset
            TargetingPasses = 0;
        }

    }
}