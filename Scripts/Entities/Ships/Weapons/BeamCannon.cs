
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class BeamCannon : Turret
    {

        public bool IsFiringLaserBeam;
        public Ship LaserBeamTarget;
        protected override void Aim()
        {
            if (IsFiringManually)
            {
                TargetPoint = Level.InputManager.GetMousePosition();
                IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);
            }
            else
            {
                if (TargetShip == null && LaserBeamTarget != null)
                {
                    TargetShip = LaserBeamTarget;
                }
                if (ShouldFire && !Utilities.HasObstaclesInTheWay(GetPosition(), TargetShip.GetPosition()))
                {
                    //Debug.Log($"Aiming {Piece.name} at {TargetShip.Name}");
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(TargetPoint), RotationRate);

                }
                else
                {
                    if (!IsFiringLaserBeam)
                    {
                        //Debug.Log($"TargetShip is null, rotating back");
                        IsAimedAtTarget = false;
                        Utilities.TimedRotation(Piece, Ship.GetRotation(), RotationRate);
                    }

                }
            }
            MoveTargetingMarker();

        }
        protected override void SetTargetShip(Ship targetShip)
        {
            if (!IsFiringLaserBeam)
            {
                //Debug.Log($"Setting target ship to {targetShip.Name}");
                TargetShip = targetShip;
            }
        }
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {
            if (!IsFiringLaserBeam)
            {
                //Debug.Log("Sending beam cannon projectile");

                
                float angle = AngleToPoint(TargetPoint);

                //Vector2 mapTransformPoint = Ship.Level.Map.transform.InverseTransformPoint(Piece.transform.position);
                //Vector2 shipOffset = Ship.GetPosition() + (Vector2) transform.position;

                //Debug.Log($"Potential spawn point for laser beam, mapTransformPoint: {mapTransformPoint}, shipOffset: {shipOffset}");

                Level.AddProjectile(ProjectilePrefab, this, GetPosition(), angle);
                Ship.FleetShip.ShotsFired++;

                if (!IsFiringManually)
                {
                    ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(TargetShip);
                    shipDamageStatus.TotalDamageSentToShip += Power;
                    LaserBeamTarget = TargetShip;
                }

                IsFiringLaserBeam = true;

            }


        }
    }
}