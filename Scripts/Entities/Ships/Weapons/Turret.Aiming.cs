using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public partial class Turret
    {
        private Vector2 _targetPoint, _frontOfShip, _colliderPoint, _globalTargetPosition, _globalTurretPosition;

        protected void MoveTargetingMarker()
        {
            if (!HasTargetingMarker)
            {
                return;
            }

            bool shouldShowMarker = Ship.Squad.IsSelected && IsAimedAtTarget && !IsFiringManually;
            if (shouldShowMarker)
            {
                TargetingMarker.transform.position = TargetPoint;
            }
            if (TargetingMarker.activeSelf != shouldShowMarker)
            {
                TargetingMarker.SetActive(shouldShowMarker);
            }
        }

        protected virtual void Aim()
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
            else if (ShouldFire)
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
                IsAimedAtTarget = false;
                if (Rotation != Ship.Rotation && (Ship.IsCeaseFire || !HasValidTarget()))
                {
                    Utilities.TimedRotation(this, Ship.Rotation, RotationRate);
                }
                IsFiringAtAsteroid = false;
            }

            MoveTargetingMarker();
        }

        protected Vector2 GetTargetPoint(Ship ship)
        {
            _targetPoint = ship.GetPosition();
            if (ShouldFireAtFrontOfShip)
            {
                _frontOfShip = _targetPoint + new Vector2(0, ship.GetHalfHeight() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ship.ShipType));
                _targetPoint = Utilities.RotatePointAroundPoint(_targetPoint, _frontOfShip, ship.Rotation * Mathf.Deg2Rad);
            }

            _globalTargetPosition = _targetPoint + Level.GetPosition();
            if (!RangeCollider.Collider.OverlapPoint(_globalTargetPosition))
            {
                _globalTurretPosition = GetPosition() + Level.GetPosition();
                _colliderPoint = ship.Collider.ClosestPoint(_globalTurretPosition);
                _targetPoint = _colliderPoint != _globalTurretPosition
                    ? _colliderPoint - Level.GetPosition()
                    : ship.GetPosition();
            }

            return _targetPoint;
        }

        protected override void SendProjectile()
        {
            if (IsFiringManually || IsFiringAtAsteroid)
            {
                SetTargetShipNull();
            }
            base.SendProjectile();
            Level.AddProjectile(ProjectileType, this, GetPosition(), AngleToPoint(TargetPoint));
            Ship.FleetShip.ShotsFired++;
            global::RlOneVsOneEpisodeCoordinator.RecordShotFired(Ship, this);
        }
    }
}
