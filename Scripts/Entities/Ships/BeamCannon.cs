
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class BeamCannon : Turret
    {

        public bool IsFiringLaserBeam;
        public Ship LaserBeamTarget;
        protected override void Aim()
        {
            if (TargetShip == null && LaserBeamTarget != null)
            {
                TargetShip = LaserBeamTarget;
            }
            if (TargetShip != null && !CeaseFire)
            {
                //Debugger.Log($"Aiming {Piece.name} at {TargetShip.Name}");
                Vector2 targetPoint = TargetShip.GetPosition();
                if (FireAtFrontOfShip)
                {
                    Vector2 frontOfShip = targetPoint + new Vector2(0, TargetShip.GetHalfHeight() - ConfigData.OffsetFromFront);
                    targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);

                }
                AimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(targetPoint), RotationRate);

            }
            else
            {
                if (!IsFiringLaserBeam)
                {
                    //Debugger.Log($"TargetShip is null, rotating back");
                    AimedAtTarget = false;
                    Utilities.TimedRotation(Piece, Ship.GetRotation(), RotationRate);
                }

            }

        }
        protected override void SetTargetShip(Ship targetShip)
        {
            if (!IsFiringLaserBeam)
            {
                //Debugger.Log($"Setting target ship to {targetShip.Name}");
                TargetShip = targetShip;
            }
        }
        protected override void SendProjectile() // [projectile-method] [note]
        {
            if (!IsFiringLaserBeam)
            {
                //Debugger.Log("Sending beam cannon projectile");

                Vector2 targetPoint = TargetShip.GetPosition();
                if (FireAtFrontOfShip)
                {
                    Vector2 frontOfShip = targetPoint + new Vector2(0, TargetShip.GetHalfHeight() - ConfigData.OffsetFromFront);
                    targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);

                }
                float angle = AngleToPoint(targetPoint);
                Level.AddProjectile(ProjectilePrefab, this, GetPosition(), angle);
                Ship.FleetShip.ShotsFired++;

                ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(TargetShip);
                shipDamageStatus.totalDamageSentToShip += Power;
                IsFiringLaserBeam = true;
                LaserBeamTarget = TargetShip;
            }


        }
    }
}