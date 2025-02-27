
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class BeamCannon : Turret
    {

        public bool IsFiringLaserBeam;
        public Ship LaserBeamTarget;
        public override void ClearData()
        {
            base.ClearData();
            IsFiringLaserBeam = false;
            LaserBeamTarget = null;
        }
        protected override void Aim()
        {
            if (IsFiringManually)
            {
                TargetPoint = Stage.InputManager.GetMousePosition();
                IsAimedAtTarget = Utilities.TimedRotation(this, GetDegreesTowardsPoint(TargetPoint), RotationRate);
            }
            else
            {
                if (TargetShip == null && LaserBeamTarget != null)
                {
                    TargetShip = LaserBeamTarget;
                }
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
                    if (!IsFiringLaserBeam)
                    {
                        //Debug.Log($"TargetShip is null, rotating back");
                        IsAimedAtTarget = false;
                        Utilities.TimedRotation(this, Ship.Rotation, RotationRate);
                    }
                    IsFiringAtAsteroid = false;

                }
            }
            MoveTargetingMarker();

        }
        protected override void SetTargetShip(Ship targetShip)
        {
            if (!IsFiringLaserBeam)
            {
                //Debug.Log($"Setting target ship to {targetShip.Name}");
                base.SetTargetShip(targetShip);
            }
        }
        private LaserBeam _beam;
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {
            if (!IsFiringLaserBeam)
            {
                //Debug.Log("Sending beam cannon projectile");



                //Vector2 mapTransformPoint = Ship.Level.Map.transform.InverseTransformPoint(Piece.transform.position);
                //Vector2 shipOffset = Ship.GetPosition() + (Vector2) transform.position;

                //Debug.Log($"Potential spawn point for laser beam, mapTransformPoint: {mapTransformPoint}, shipOffset: {shipOffset}");

                //Projectile beam = Level.AddProjectile(ConfigData.ProjectileTypes.Beam, this, GetPosition(), angle);

                _beam = (LaserBeam) Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.Beam);
                _beam.Transform.parent = Level.Map.transform;

                //Debug.Log($"Position before setup for {projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
                _beam.Setup(Level, this, Ship, TargetShip, GetPosition(), AngleToPoint(TargetPoint), Range, Power);
                Ship.ProjectilesInFlight.Add(_beam);

                if (Ship.Squad.HasCustomColor)
                {
                    _beam.SpriteRenderer.color = Ship.Squad.Color;
                }

                Ship.FleetShip.ShotsFired++;
                if (!IsFiringManually && !IsFiringAtAsteroid)
                {
                    Level.State.GetShipDamageStatus(Side, TargetShip).TotalDamageSentToShip += Power;
                    LaserBeamTarget = TargetShip;
                }
                IsFiringLaserBeam = true;
            }


        }
    }
}