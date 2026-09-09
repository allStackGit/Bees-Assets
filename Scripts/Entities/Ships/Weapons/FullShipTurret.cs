using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// This is for laserbuilder turrets that don't rotate independently of the ship they're on, such as the flagship main cannon. Behaves just like a regular turret but the ship itself moves
    /// </summary>
    public class FullShipTurret : LaserBuilder
    {
        private Vector3 _rightRotationRate, _leftRotationRate;
        //private AudioSource _chargingSound;
        public override void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Create(ship, type, weaponSound, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
            _rightRotationRate = new Vector3(0, 0, 1 * Stage.FixedDeltaTime * RotationRate);
            _leftRotationRate = new Vector3(0, 0, 1 * Stage.FixedDeltaTime * RotationRate * -1);


            //_chargingSound = Instantiate(Stage.Audio.FlagshipLaserChargingSound);
            //_chargingSound.transform.parent = PieceTransform;
            //_chargingSound.transform.localPosition = Vector2.zero;
        }
        public override void ResetRotation()
        {
            Rotation = OriginalRotation;
        }
        protected override void Aim()
        {
            if (IsRlControlled)
            {
                AimRlMainCannon();
                return;
            }

            if (!Ship.IsMoving)
            {
                if (IsFiringManually)
                {
                    TargetPoint = Stage.InputManager.GetMousePosition();
                    IsAimedAtTarget = RotateShipTowardsTargetPoint(GetDegreesTowardsPoint(TargetPoint));
                }
                else
                {
                    if (ShouldFire)
                    {
                        TargetPoint = GetTargetPoint(TargetShip);
                        IsAimedAtTarget = RotateShipTowardsTargetPoint(GetDegreesTowardsPoint(TargetPoint));
                    }
                    else
                    {
                        IsAimedAtTarget = false;
                        if (Ship.IsCeaseFire || !HasValidTarget())
                        {
                            //Debug.Log($"{Name} has no ships to fire at, returning to default aim");
                            RotateShipTowardsTargetPoint(Ship.Rotation);
                        }
                    }
                }
            }
            else
            {
                if (IsFiringManually)
                {
                    TargetPoint = Stage.InputManager.GetMousePosition();
                    IsAimedAtTarget = Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));
                }
                else if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));
                }
                else
                {
                    IsAimedAtTarget = false;
                }

            }

            if (!IsAimedAtTarget)
            {
                //LaserBuilderAnimation.SetActive(false);
                Animator.speed = 0;
                //_chargingSound.Stop();
            }
            else
            {
                LaserBuilderAnimation.SetActive(true);
                Animator.speed = 1;
                //if (!_chargingSound.isPlaying)
                //{
                //    _chargingSound.Play();
                //}
            }

            MoveTargetingMarker();

        }
        private void AimRlMainCannon()
        {
            // The policy owns the hull. This method observes whether the current hull/cannon heading
            // happens to line up with the requested point for diagnostics, but never rotates or slows
            // the ship. A queued shot is allowed to charge regardless of that alignment.
            TargetPoint = RlTargetPoint;
            IsFiringAtAsteroid = false;
            IsAimedAtTarget = Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));
            Animator.speed = 1f;
            MoveTargetingMarker();
        }
        protected override bool CanAcceptRlFireRequest()
        {
            // Unlike an independently rotating turret, this fixed cannon must not hide hull aiming
            // behind scripted assistance. If the weapon is ready, the policy may commit the shot at
            // any heading and learn from the resulting hit or miss.
            return true;
        }
        private float _difference;
        private static Vector3 _forward = Vector3.forward;
        protected bool RotateShipTowardsTargetPoint(float rotation)
        {
            _difference = Mathf.DeltaAngle(Ship.Rotation, rotation);
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (_difference > 3)
            {
                RotateShipAndTurrets(_rightRotationRate);

                if (Ship.HasRocketFlares)
                {
                    Ship.RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(true);
                    });

                    Ship.LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });
                }

            }
            else if (_difference < -3)
            {
                RotateShipAndTurrets(_leftRotationRate);

                if (Ship.HasRocketFlares)
                {
                    Ship.RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    Ship.LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(true);
                    });
                }

            }
            else
            {
                SnapShipAndTurretsToRotation(rotation);

                if (Ship.HasRocketFlares)
                {
                    Ship.RightRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });

                    Ship.LeftRocketFlares.ForEach((flare) =>
                    {
                        flare.SetActive(false);
                    });
                }


                return true;
            }

            return false;
        }
        private void RotateShipAndTurrets(Vector3 rotationDelta)
        {
            PieceTransform.Rotate(rotationDelta);
            Ship.Rotation += rotationDelta.z;
            Ship.Turrets.ForEach((turret) => turret.Rotation += rotationDelta.z);
        }
        private void SnapShipAndTurretsToRotation(float rotation)
        {
            float rotationDelta = Mathf.DeltaAngle(Ship.Rotation, rotation);
            PieceTransform.localEulerAngles = _forward * rotation;
            Ship.Rotation = rotation;
            Ship.Turrets.ForEach((turret) => turret.Rotation += rotationDelta);
        }
        protected override void SendProjectile()
        {
            base.SendProjectile();
            if (!IsRlControlled)
            {
                LaserBuilderAnimation.SetActive(false);
            }
            //_chargingSound.Stop();
        }
        protected override bool CanCompleteQueuedShot()
        {
            if (!IsRlControlled || !IsRlShotQueued)
            {
                return base.CanCompleteQueuedShot();
            }

            // Turret.SendProjectile launches toward TargetPoint. For the fixed RL cannon, replace the
            // policy's diagnostic aim point at the final animation event with a point directly ahead
            // of the cannon's current physical heading. The ship may keep moving and turning while it
            // charges, so the heading at the instant of firing determines the actual shot direction.
            TargetPoint = GetRlForwardFirePoint();
            return true;
        }
        private Vector2 GetRlForwardFirePoint()
        {
            float radians = Rotation * Mathf.Deg2Rad;
            Vector2 forwardDirection = new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
            return GetPosition() + forwardDirection;
        }
    }
}