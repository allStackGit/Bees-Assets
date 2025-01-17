
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class DualCannon : Turret
    {

        public Vector2 LeftCannonOffset = new Vector2(-1.1f, .5f);
        public Vector2 RightCannonOffset = new Vector2(1.1f, .5f);
        //public GameObject RangeCircleLeft; // For the second cannon in the dual cannon, the first one being the right one
        //public CircleCollider2D RangeColliderLeft; // Likewise for the range collider
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {

            //get the angle to the target ship
            float angle = Ship.AngleToPoint(TargetPoint);

            // get the targetShipPosition of the cannons
            Vector2 shipPosition = Ship.GetPosition();
            Vector2 position = GetPosition();
            Vector2 leftCannonPosition = position + LeftCannonOffset;
            Vector2 rightCannonPosition = position + RightCannonOffset;

            // get the angle of the cannons
            float cannonAngle = GetRotation() * Mathf.Deg2Rad;

            // calculate the rotated targetShipPosition of the end of the cannons
            Vector2 rotatedLeftCannonPosition = Utilities.RotatePointAroundPoint(shipPosition, leftCannonPosition, cannonAngle);
            Vector2 rotatedRightCannonPosition = Utilities.RotatePointAroundPoint(shipPosition, rightCannonPosition, cannonAngle);

            // instantiate the projectiles
            Level.AddProjectile(ProjectilePrefab, this, rotatedLeftCannonPosition, angle);
            Level.AddProjectile(ProjectilePrefab, this, rotatedRightCannonPosition, angle);
            Ship.FleetShip.ShotsFired += 2;

            // Set the damage status
            if (!IsFiringManually && !IsFiringAtAsteroid)
            {
                ShipDamageStatus shipDamageStatus = Level.GetState().GetShipDamageStatus(Side, TargetShip);
                shipDamageStatus.TotalDamageSentToShip += Power * 2;
            }

        }

        // UI Methods
        public override void ShowRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(true);
                //RangeCircleLeft.SetActive(true);
            }
        }

        public override void HideRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(false);
                //RangeCircleLeft.SetActive(false);
            }
        }

    }
}