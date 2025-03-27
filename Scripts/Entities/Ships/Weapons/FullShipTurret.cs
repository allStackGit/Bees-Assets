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
        public override void Create(Ship ship, ConfigData.WeaponTypes type, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Create(ship, type, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
            _rightRotationRate = new Vector3(0, 0, 1 * Stage.FixedDeltaTime * RotationRate);
            _leftRotationRate = new Vector3(0, 0, 1 * Stage.FixedDeltaTime * RotationRate * -1);
        }
        public override void ResetRotation()
        {
            Rotation = OriginalRotation;
        }
        protected override void Aim()
        {
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

            }

            if (!IsAimedAtTarget)
            {
                LaserBuilderAnimation.SetActive(false);
            }
            else
            {
                LaserBuilderAnimation.SetActive(true);
            }

            MoveTargetingMarker();

        }
        private float _difference;
        private static Vector3 _forward = Vector3.forward;
        protected bool RotateShipTowardsTargetPoint(float rotation)
        {
            _difference = Mathf.DeltaAngle(Ship.Rotation, rotation);
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (_difference > 3)
            {
                PieceTransform.Rotate(_rightRotationRate);
                Ship.Rotation += _rightRotationRate.z;

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
                PieceTransform.Rotate(_leftRotationRate);
                Ship.Rotation += _leftRotationRate.z;

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
                PieceTransform.localEulerAngles = _forward * rotation;
                Ship.Rotation = rotation;

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
    }
}