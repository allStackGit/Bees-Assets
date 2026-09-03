
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
            if (IsRlControlled)
            {
                TargetPoint = RlTargetPoint;
                IsAimedAtTarget = Utilities.TimedRotation(this, GetDegreesTowardsPoint(TargetPoint), RotationRate);
                IsFiringAtAsteroid = false;
            }
            else if (IsFiringManually)
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
                        IsAimedAtTarget = false;
                        Utilities.TimedRotation(this, Ship.Rotation, RotationRate);
                    }
                    IsFiringAtAsteroid = false;

                }
            }
            MoveTargetingMarker();

        }
        public float GetLocalRotation()
        {
            return PieceTransform.localEulerAngles.z;
        }
        protected override void SetTargetShip(Ship targetShip)
        {
            if (!IsFiringLaserBeam)
            {
                base.SetTargetShip(targetShip);
            }
        }
        private LaserBeam _beam;
        protected override void SendProjectile()
        {
            if (!IsFiringLaserBeam)
            {
                _beam = (LaserBeam) Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.Beam);
                _beam.Transform.parent = Level.Map.Transform;

                if (IsFiringManually || IsFiringAtAsteroid)
                {
                    SetTargetShipNull();
                }

                _beam.Setup(Level, this, Ship, TargetShip, GetPosition(), AngleToPoint(TargetPoint), Range, Power);
                Ship.ProjectilesInFlight.Add(_beam);

                if (Ship.Squad.HasCustomColor)
                {
                    _beam.SpriteRenderer.color = Ship.Squad.Color;
                }
                else if (!Stage.IsTraining)
                {
                    _beam.SpriteRenderer.color = Color.white;
                }

                Ship.FleetShip.ShotsFired++;
                global::RlOneVsOneEpisodeCoordinator.RecordShotFired(Ship, this);
                if (!IsFiringManually && !IsFiringAtAsteroid)
                {
                    Level.State.GetShipDamageStatus(Side, TargetShip).TotalDamageSentToShip += Power;
                    LaserBeamTarget = TargetShip;
                }
                IsFiringLaserBeam = true;
                PlaySoundEffect();
            }
        }
    }
}
