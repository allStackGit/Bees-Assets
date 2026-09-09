using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// This is for laserbuilder turrets that don't rotate independently of the ship they're on, such as the flagship main cannon. Behaves just like a regular turret but the ship itself moves
    /// </summary>
    public class FullShipTurret : LaserBuilder
    {
        private Vector3 _rightRotationRate, _leftRotationRate;
        private bool _rlHullAimActive;
        private Vector2 _rlCommittedTargetPoint;
        private float _rlPreviousCurrentSpeed, _rlPreviousRotationSpeed;
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
        public override void ClearData()
        {
            ReleaseRlHullAim();
            _rlCommittedTargetPoint = Vector2.zero;
            base.ClearData();
        }
        public override void Deactivate()
        {
            ReleaseRlHullAim();
            base.Deactivate();
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
            bool shouldOwnHullAim = IsRlShotQueued || (RlFireRequested && ReadyToFire && !Ship.IsCeaseFire);
            if (shouldOwnHullAim)
            {
                BeginRlHullAim();
                MaintainRlHullAim();
            }
            else
            {
                ReleaseRlHullAim();
            }

            TargetPoint = IsRlShotQueued ? _rlCommittedTargetPoint : RlTargetPoint;
            IsFiringAtAsteroid = false;
            IsAimedAtTarget = _rlHullAimActive
                ? RotateShipTowardsTargetPoint(GetDegreesTowardsPoint(TargetPoint))
                : Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));

            // LaserBuilder.SendProjectile owns activation for queued RL shots. Aim only controls
            // whether an active charge animation may advance while the hull is on target.
            Animator.speed = IsAimedAtTarget ? 1 : 0;
            MoveTargetingMarker();
        }
        private void BeginRlHullAim()
        {
            if (_rlHullAimActive)
            {
                return;
            }

            _rlHullAimActive = true;
            _rlPreviousCurrentSpeed = Ship.CurrentSpeed;
            _rlPreviousRotationSpeed = Ship.RotationSpeed;
        }
        private void MaintainRlHullAim()
        {
            if (!_rlHullAimActive)
            {
                return;
            }

            // Preserve the policy's Direction/order while temporarily giving the fixed cannon the
            // hull. Zeroing speed and normal hull-turn rate prevents navigation from fighting the
            // cannon; both are restored as soon as this shot resolves or is cancelled.
            Ship.CurrentSpeed = 0f;
            Ship.RotationSpeed = 0f;
            Ship.Body.linearVelocity = Vector2.zero;
            Ship.IsMoving = false;
        }
        private void ReleaseRlHullAim()
        {
            if (!_rlHullAimActive)
            {
                return;
            }

            Ship.CurrentSpeed = _rlPreviousCurrentSpeed;
            Ship.RotationSpeed = _rlPreviousRotationSpeed;
            _rlHullAimActive = false;
        }
        protected override void OnRlControlUpdated()
        {
            if ((!RlFireRequested || Ship.IsCeaseFire) && !IsRlShotQueued)
            {
                ReleaseRlHullAim();
            }
        }
        protected override void OnRlControlCleared()
        {
            ReleaseRlHullAim();
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
            if (IsRlControlled)
            {
                _rlCommittedTargetPoint = TargetPoint;
                BeginRlHullAim();
                MaintainRlHullAim();
            }

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

            TargetPoint = _rlCommittedTargetPoint;
            return Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));
        }
        protected override void OnShotResolved(bool fired)
        {
            base.OnShotResolved(fired);
            ReleaseRlHullAim();
        }
    }
}