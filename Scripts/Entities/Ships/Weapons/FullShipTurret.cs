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
            _rightRotationRate = new Vector3(0, 0, 1 * Time.fixedDeltaTime * RotationRate);
            _leftRotationRate = new Vector3(0, 0, 1 * Time.fixedDeltaTime * RotationRate * -1);
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
                        if (CeaseFire || !HasValidTarget())
                        {
                            //Debug.Log($"{Name} has no ships to fire at, returning to default aim");
                            RotateShipTowardsTargetPoint(Ship.GetRotation());
                        }
                    }
                }
            }
            else
            {
                if (IsFiringManually)
                {
                    TargetPoint = Stage.InputManager.GetMousePosition();
                }
                else if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                }
                IsAimedAtTarget = Utilities.IsRotatedTowards(Piece, GetDegreesTowardsPoint(TargetPoint));

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
        protected bool RotateShipTowardsTargetPoint(float rotation)
        {
            _difference = Mathf.DeltaAngle(Piece.transform.eulerAngles.z, rotation);
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (_difference > 3)
            {
                Piece.transform.Rotate(_rightRotationRate);

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
                Piece.transform.Rotate(_leftRotationRate);

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
                Piece.transform.eulerAngles = new Vector3(0, 0, rotation);

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