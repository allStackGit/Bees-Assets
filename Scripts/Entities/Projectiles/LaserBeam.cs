

using Assets.Scripts.Entities.Ships;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using Assets.Scripts.Levels;

namespace Assets.Scripts.Entities.Projectiles
{
    public class LaserBeam : Projectile
    {
        private float _scale = 2f;
        private int _powerLoss;
        private HashSet<Ship> _shipsHit = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        private Vector2 _oneAndHalf = new Vector2(1, .5f);
        private BeamCannon BeamCannon;
        public void Setup(Level level, BeamCannon beamCannon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            base.Setup(level, beamCannon, shooter, target, startingPosition, angle, range, power);
            BeamCannon = beamCannon;
            _halfRange = Range / 2;
        }

        public override void ClearData()
        {
            base.ClearData();
            _powerLoss = 0;
            Transform.localScale = _oneAndHalf;
            _shipsHit.Clear();
            Angle = 0;
        }
        private int _halfHealth;

        public override void ContactTarget(Ship target)
        {
            if (_shipsHit.Contains(target))
            {
                return;
            }

            _shipsHit.Add(target);
            _halfHealth = target.Health / 2;
            _powerLoss = Mathf.Clamp(_halfHealth, 0, Power);
            if (Power <= _halfHealth)
            {
                Kill();
                return;
            }

            Power -= _powerLoss;
            _powerLoss = 0;
        }
        public override void Kill()
        {
            if (!IsDead)
            {
                IsDead = true;
                BeamCannon.IsFiringLaserBeam = false;
                BeamCannon.LaserBeamTarget = null;
                BeamCannon.StopSoundEffect();
                RemoveDamageSentEntry();
                Shooter?.ProjectilesInFlight.Remove(this);
                Level.State.RemoveProjectile(this);
                Deactivate();
                Stage.Pool.ReturnProjectileToPool(this);
            }
        }
        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (!IsDead)
            {
                ExtendBeam();
            }
        }

        private float _worldAngle, _localAngle;
        private Vector2 _laserBeamOffset, _localCannonPoint, _rotatedLocalPosition, _offsetRotatedCannonPosition;
        private Quaternion _localRotation;
        private float _halfRange;
        private Vector3 _forward = Vector3.forward;
        private void ExtendBeam()
        {
            if (!Shooter.IsDead)
            {
                if (Transform.localScale.x < _halfRange)
                {
                    Transform.localScale = new Vector2(Transform.localScale.x + _scale, Transform.localScale.y);

                    _worldAngle = BeamCannon.Rotation + 90;
                    _localAngle = BeamCannon.GetLocalRotation() + 90;

                    _laserBeamOffset = new Vector2(Transform.localScale.x, 0);
                    _localRotation = Quaternion.Euler(0, 0, _localAngle);
                    _localCannonPoint = Vector2.zero + _laserBeamOffset;
                    _rotatedLocalPosition = _localRotation * _localCannonPoint;
                    _offsetRotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(_rotatedLocalPosition) - Level.GetPosition();
                    Angle = _worldAngle * -Mathf.Deg2Rad;

                    Transform.localEulerAngles = _forward * _worldAngle;
                    Transform.localPosition = _offsetRotatedCannonPosition;
                    Body.linearVelocity = Shooter.Body.linearVelocity;
                    return;
                }
            }
            if (!BeamCannon.IsFiringManually)
            {
                RemoveDamageSentEntry();
            }
            Kill();
        }
    }
}
