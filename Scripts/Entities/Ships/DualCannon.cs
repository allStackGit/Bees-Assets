
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class DualCannon : Turret
    {

        public Vector2 LeftCannonOffset = new Vector2(-1.1f, .5f);
        public Vector2 RightCannonOffset = new Vector2(1.1f, .5f);
        protected override void SendProjectile() // [projectile-method] [note]
        {

            //get the angle to the target ship
            Vector2 targetShipPosition = TargetShip.GetPosition();
            if (FireAtFrontOfShip)
            {
                Vector2 frontOfShip = targetShipPosition + new Vector2(0, TargetShip.GetHalfHeight() - ConfigData.OffsetFromFront);
                targetShipPosition = Utilities.RotatePointAroundPoint(targetShipPosition, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);
            }


            float angle = Ship.AngleToPoint(targetShipPosition);

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
            ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(TargetShip);
            shipDamageStatus.totalDamageSentToShip += Power * 2;
        }

    }
}