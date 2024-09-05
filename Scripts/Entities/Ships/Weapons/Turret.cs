

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
        public int TargetingPasses, PassesPerFire;
        /// <summary> How many times the targeting sequence runs per fire sequence </summary>
        public float TargetingRate;
        public float DamagePerSecond => RateOfFire > 0 ? (Power / RateOfFire) : 0;
        public Vector2 TargetPoint;
        public GameObject TargetingMarker;


        public virtual void Setup(Ship ship, string type, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Setup(ship, type, range, power, 0, rateOfFire, projectileValue, piece, projectilePrefab);
            ShouldFireAtFrontOfShip = fireAtFrontOfShip;
            PassesPerFire = 3;
            TargetingRate = RateOfFire / PassesPerFire;
            RotationRate = rotationRate;

            if (Ship.IsUserControlled)
            {
                TargetingMarker = Instantiate(Level.TargetingMarkerPrefab, Vector2.zero, Quaternion.identity);
                TargetingMarker.transform.SetParent(Level.Map.transform);
                TargetingMarker.SetActive(false);
                TargetingMarker.name = $"{Name}'s Targeting Marker";
                HasTargetingMarker = true;
            }

        }
        private void Start()
        {
            if (RateOfFire > 0)
            {
                //Debug.Log($"Aiming rate: {TargetingRate} for {FleetShip.Name}");
                InvokeRepeating(nameof(TargetingSequence), TargetingRate, TargetingRate);
                //Invoke(nameof(Fire), RateOfFire);
            }
        }
        protected void MoveTargetingMarker()
        {
            if (HasTargetingMarker && Squad.IsSelected && IsAimedAtTarget)
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
            if (TargetingPasses == PassesPerFire)
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
                SetTargetShip(null);
            }
        }
        /// <summary>
        /// Run on FixedUpdate(). Aims the Turret at the TargetShip, if one exists
        /// </summary>
        protected virtual void Aim()
        {
            if (IsFiringManually)
            {
                TargetPoint = Level.InputManager.GetMousePosition();
                IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);
            }
            else
            {
                if (ShouldFire && !Utilities.HasObstaclesInTheWay(GetPosition(), TargetShip.GetPosition()))
                {
                    //Debug.Log($"Aiming {Piece.name}");
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);

                }
                else
                {
                    IsAimedAtTarget = false;
                    if (!HasShipsWithinRange || CeaseFire)
                    {
                        //Debug.Log($"{Name} has no ships to fire at, returning to default aim");
                        Utilities.TimedRotation(Piece, Ship.GetRotation(), RotationRate);
                    }
                }
            }
            MoveTargetingMarker();


        }
        /// <summary>
        /// Sends the projectile to the Target Ship. Last in the Targeting sequence.
        /// </summary>
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {
            base.SendProjectile();
            //Debug.Log("Sending turret projectile");
            float angle = AngleToPoint(TargetPoint);

            Level.AddProjectile(ProjectilePrefab, this, GetPosition(), angle);
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
                Vector2 frontOfShip = targetPoint + new Vector2(0, ship.GetHalfHeight() - ConfigData.OffsetFromFront);
                targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, ship.GetRotation() * Mathf.Deg2Rad);

            }
            if (!RangeCollider.Collider.OverlapPoint(targetPoint+Level.GetPosition()))
            {
                //Debug.Log($"{Ship.Name} is firing at {ship.Name} but the target point is not within range");
                targetPoint = ship.Collider.ClosestPoint(GetPosition());
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
            if (IsFiringManually)
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
                            //Debug.Log($"{Name} was not aimed at it's target but was aimed at another target: {TargetShip.Name}. Firing");
                            Fire();

                        }
                        else
                        {
                            //Debug.Log($"{Ship.Name} is not firing {Name} because the piece is not aimed at any target");
                            __NotShootingReason = $"{Ship.Name} is not firing {Name} because the piece is not aimed at any target";
                            //Invoke(nameof(FireNext), .1f);
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