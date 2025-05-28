

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

using UnityEngine;
using static UnityEngine.GraphicsBuffer;

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
        /// <summary>
        /// The current rotation of the turret in degrees (Should match PieceTransform.eulerAngles.z)
        /// </summary>
        public float Rotation;
        /// <summary>
        /// The original rotation of the turret in degrees for resetting the rotation of the turret. Most likely a value of 0.
        /// </summary>
        public float OriginalRotation;
        /// <summary>
        /// The turret's power / rate of fire. Used only for debugging purposes
        /// </summary>
        public float DamagePerSecond;
        /// <summary>
        /// The point on the map that the target is at, either the target ship or the target asteroid, or if firing manually, the point on the map that the mouse is at
        /// </summary>
        public Vector2 TargetPoint;
        public GameObject TargetingMarker;
        public CollisionAsteroid TargetAsteroid;
        public bool HasTargetAsteroid;
        public bool IsFiringAtAsteroid;

        /// <summary>
        /// Whether or not the ship should fire at the asteroid: If it's not cease fire and it has a target asteroid, and it does not have a target ship
        /// </summary>
        public bool ShouldFireAtAsteroid => !Ship.IsCeaseFire && HasTargetAsteroid && TargetShip == null && !TargetAsteroid.IsDead;

        //public override bool ShouldFire => TargetShip != null && !CeaseFire && IsShipValidTarget(TargetShip);

        public virtual void Create(Ship ship, ConfigData.WeaponTypes type, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType,
            bool fireAtFrontOfShip, float rotationRate)
        {
            base.Create(ship, type, range, power, 0, rateOfFire, projectileValue, piece, projectileType);
            OriginalRotation = PieceTransform.eulerAngles.z;
            Rotation = OriginalRotation;
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

        private ScaledTimer _targetingSequenceTimer = new ScaledTimer();
        public override void Setup()
        {
            base.Setup();
            if (Ship.IsUserControlled)
            {
                TargetingMarker.transform.SetParent(Level.Map.Transform);
                TargetingMarker.name = $"{Name}'s Targeting Marker";
            }
            if (RateOfFire > 0)
            {
                //Debug.Log($"Aiming rate: {TargetingRate} for {FleetShip.Name}");
                _targetingSequenceTimer.Reuse(TargetingRate, TargetingSequence, true);
                Level.AddTimer(_targetingSequenceTimer);
                //InvokeRepeating(nameof(TargetingSequence), TargetingRate, TargetingRate);
                //Invoke(nameof(Fire), RateOfFire);
            }

        }
        public override void Deactivate()
        {
            base.Deactivate();
            if (HasTargetingMarker)
            {
                TargetingMarker.SetActive(false);
            }
        }
        public override void CancelTimer()
        {
            Level.CancelTimer(_targetingSequenceTimer);
            base.CancelTimer();
        }

        public override void ClearData()
        {
            base.ClearData();
            ResetRotation();
            IsAimedAtTarget = false;
            IsFiringManually = false;
            ReadyToFire = false;
            TargetPoint = Vector2.zero;
            TargetAsteroid = null;
            HasTargetAsteroid = false;
            IsFiringAtAsteroid = false;
        }
        public virtual void ResetRotation()
        {
            Rotation = OriginalRotation;
            PieceTransform.eulerAngles = new Vector3(0, 0, OriginalRotation);
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
            if (!Ship.IsCeaseFire)
            {
                Aim();
            }
        }
        /// <summary>
        /// First in the Targeting sequence. On Passes #1 and #2 it runs Targeting. On Pass #3 it runs TryToFire()
        /// </summary>
        private void TargetingSequence()
        {
            //__NotShootingReason = $"Reset Reason.";
            TargetingPasses++;
            if ((ReadyToFire && IsAimedAtTarget) || TargetingPasses == PassesPerFire)
            {
                //Debug.Log($"{Name} hit {TargetingPasses} targeting passes, now firing");
                TryToFire();
            }
            else if (!IsFiringManually)
            {
                if (!Ship.IsCeaseFire)
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
                IsAimedAtTarget = Utilities.TimedRotation(this, GetDegreesTowardsPoint(TargetPoint), RotationRate);
            }
            else
            {
                if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = Utilities.TimedRotation(this, GetDegreesTowardsPoint(TargetPoint), RotationRate);
                    IsFiringAtAsteroid = false;
                }
                else if (ShouldFireAtAsteroid)
                {
                    TargetPoint = TargetAsteroid.GetPosition();
                    IsAimedAtTarget = Utilities.TimedRotation(this, GetDegreesTowardsPoint(TargetPoint), RotationRate);
                    IsFiringAtAsteroid = true;
                }
                else
                {
                    IsAimedAtTarget = false;
                    if ((Ship.IsCeaseFire || !HasValidTarget()) && Rotation != Ship.Rotation)
                    {
                        //Debug.Log($"{Name} has no ships to fire at, returning to default aim");
                        Utilities.TimedRotation(this, Ship.Rotation, RotationRate);
                    }
                    IsFiringAtAsteroid = false;
                }
            }
            MoveTargetingMarker();


        }
        //private Ship _validTarget;
        private int _index;
        private IEnumerable<Ship> _shipsWithinRange;
        /// <summary>
        /// Checks all ships within range to see if this turret can fire upon them
        /// </summary>
        /// <returns></returns>
        public bool HasValidTarget()
        {
            _shipsWithinRange = ShipsWithinRange.Values;
            for (_index = 0; _index < ShipsWithinRange.Count; _index++)
            {
                if (IsShipValidTarget(_shipsWithinRange.ElementAt(_index)))
                {
                    return true;
                }
            }
            return false;
            //return ShipsWithinRange.Any((targetShip) => IsShipValidTarget(targetShip.Value));
        }
        /// <summary>
        /// Checks if the ship is a) alive, b within range, c) In the map, and d) Not blocked by obstacles
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

            Level.AddProjectile(ProjectileType, this, GetPosition(), AngleToPoint(TargetPoint));
            Ship.FleetShip.ShotsFired++;

        }
        private Vector2 _targetPoint, _frontOfShip, _colliderPoint, _globalTargetPosition, _globalTurretPosition;
        /// <summary>
        /// Gets the target point on a ship that should be fired at
        /// </summary>
        /// <param name="ship"></param>
        /// <returns></returns>
        protected Vector2 GetTargetPoint(Ship ship)
        {
            _targetPoint = ship.GetPosition();
            if (ShouldFireAtFrontOfShip)
            {
                _frontOfShip = _targetPoint + new Vector2(0, ship.GetHalfHeight() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ship.ShipType));
                _targetPoint = Utilities.RotatePointAroundPoint(_targetPoint, _frontOfShip, ship.Rotation * Mathf.Deg2Rad);
    //            Debug.Log($"{Ship.Name} is firing at {ship.Name}, positioned at {ship.GetPosition()} and unrotated front of ship is {_frontOfShip} and the rotated target" +
    //$" point is {_targetPoint}");


            }
            _globalTargetPosition = _targetPoint + Level.GetPosition();
            if (!RangeCollider.Collider.OverlapPoint(_globalTargetPosition))
            {
                //Debug.Log($"{Name} is firing at {ship.Name} but the target is not within range: {_targetPoint}");

                _globalTurretPosition = GetPosition() + Level.GetPosition();
                _colliderPoint = ship.Collider.ClosestPoint(_globalTurretPosition);
                if (_colliderPoint != _globalTurretPosition)
                {
                    _targetPoint = _colliderPoint - Level.GetPosition();
                }
                else
                {
                    //Debug.Log($"{Name} is firing at {ship.Name} but we could not find a point on the collider that wasn't our own point. Are we inside the collider? Should we just fire at the center of the ship?");
                    _targetPoint = ship.GetPosition();
                }
                //Debug.Log($"{Name} is firing at {ship.Name} but the target point is not within range. The new target point is: {_targetPoint}");

            }
            //if (!RangeCollider.Collider.OverlapPoint(_globalTargetPosition) && Level.DistanceOutOfBounds(_targetPoint) > 16)
            //{
            //    Debug.LogWarning($"{Name} is firing at {ship.Name} but the target point is out of bounds: {_targetPoint}");
            //}
            return _targetPoint;
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
                if (!ship.IsDead)
                {
                    if (Utilities.IsAimedAt(this, GetDegreesTowardsPoint(GetTargetPoint(ship))))
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
        private Ship _potentialTargetShip;
        /// <summary>
        /// Tries to fire if the weapon has a valid target. Second in the targeting sequence but only called on the 3rd pass
        /// </summary>
        protected void TryToFire()
        {
            //Debug.Log($"{Name} trying to fire");
            if (IsFiringManually || IsFiringAtAsteroid)
            {
                if (IsAimedAtTarget && !Ship.IsCeaseFire)
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
                        //__NotShootingReason = $"{Ship.Name} is not firing {Name} because the piece is not aimed at the target: {TargetShip.Name}";
                        _potentialTargetShip = GetAimedAtTarget();
                        if (_potentialTargetShip != null)
                        {
                            SetTargetShip(_potentialTargetShip);
                            Fire();
                            //Debug.Log($"{Name} was not aimed at it's target but was aimed at another target: {TargetShip.Name}. Firing");

                        }
                        else
                        {
                            //Debug.Log($"{Ship.Name} is not firing {Name} because the piece is not aimed at any target");
                            //__NotShootingReason = $"{Ship.Name} is not firing {Name} because the piece is not aimed at any target";
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
                        //__NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because the TargetShip is null";
                        FireNext();
                    }
                    //else if (CeaseFire)
                    //{
                    //    //Debug.Log($"{Ship.Name} is not firing {Piece.name} because CeaseFire is on");
                    //    __NotShootingReason = $"{Ship.Name} is not firing {Name} because CeaseFire is on";
                    //}
                    //Invoke(nameof(Fire), RateOfFire);
                }
            }

            // Reset
            TargetingPasses = 0;
        }
        public override Vector2 GetPosition()
        {
            //Debug.Log($"Turret piece has position of {Piece.transform.position}, local position of {Piece.transform.localPosition}, and " +
                //$"inverseTransform position of {Ship.Level.Map.Transform.InverseTransformPoint(Piece.transform.position)}. Local+Ship: {Ship.GetPosition()+(Vector2)Piece.transform.localPosition}");
            
            return Ship.Level.Map.Transform.InverseTransformPoint(PieceTransform.position);
            //try
            //{
            //    return Ship.Level.Map.Transform.InverseTransformPoint(Piece.transform.position);
            //}
            //catch (Exception e)
            //{
            //    Debug.Log($"Ship: {Ship}, Level: {Ship?.Level}, Map: {Ship?.Level?.Map}, Piece: {Piece}");
            //    throw e;
            //}

        }

    }
}