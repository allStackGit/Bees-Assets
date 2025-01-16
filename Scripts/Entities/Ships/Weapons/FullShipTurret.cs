using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// This is for laserbuilder turrets that don't rotate independently of the ship they're on, such as the flagship main cannon. Behaves just like a regular turret but the ship itself moves
    /// </summary>
    public class FullShipTurret : LaserBuilder
    {

        protected override void Aim()
        {
            if (!Ship.IsMoving)
            {
                if (IsFiringManually)
                {
                    TargetPoint = Level.InputManager.GetMousePosition();
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
                    TargetPoint = Level.InputManager.GetMousePosition();
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

        protected bool RotateShipTowardsTargetPoint(float rotation)
        {
            float difference = Mathf.DeltaAngle(Piece.transform.eulerAngles.z, rotation);
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (difference > 3)
            {
                Piece.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * RotationRate));

                Ship.RightRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(true);
                });

                Ship.LeftRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(false);
                });
            }
            else if (difference < -3)
            {
                Piece.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * RotationRate * -1));

                Ship.RightRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(false);
                });

                Ship.LeftRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(true);
                });
            }
            else
            {
                Piece.transform.eulerAngles = new Vector3(0, 0, rotation);

                Ship.RightRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(false);
                });

                Ship.LeftRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(false);
                });

                return true;
            }

            return false;
        }
    }
}