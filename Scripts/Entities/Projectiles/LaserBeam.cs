

using Assets.Scripts.Entities.Ships;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using Assets.Scripts.Levels;

namespace Assets.Scripts.Entities.Projectiles
{
    public class LaserBeam : Projectile
    {

        private Vector2 _lastShooterPosition, _lastTargetPoint;
        private float _scale = 2f;
        private Ship _target;
        private int _powerLoss;
        private HashSet<Ship> _shipsHit = new HashSet<Ship>();
        private Vector2 _oneAndHalf = new Vector2(1, .5f);
        private BeamCannon BeamCannon;
        public void Setup(Level level, BeamCannon beamCannon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            base.Setup(level, beamCannon, shooter, target, startingPosition, angle, range, power);
            BeamCannon = beamCannon;
            _lastShooterPosition = Weapon.GetPosition();
            _target = Weapon.TargetShip;
            if (_target != null)
            {
                _lastTargetPoint = _target.GetPosition();
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            _lastShooterPosition = Vector2.zero;
            _lastTargetPoint = Vector2.zero;
            _target = null;
            _powerLoss = 0;
            transform.localScale = _oneAndHalf;
            _shipsHit.Clear();
            Angle = 0;
        }
        private int _halfHealth;
        public override void ContactTarget(Ship target)
        {
            if (!_shipsHit.Contains(target))
            {
                _shipsHit.Add(target);
                //Debug.Log($"{Name} hit {target.Name}");
                _halfHealth = target.Health / 2;
                _powerLoss = Mathf.Clamp(_halfHealth, 0, Power);
                if (Power <= _halfHealth)
                {
                    Kill();
                }
                Power -= _powerLoss;
                //Debug.Log($"{Name} lost {_powerLoss} power and now has {Power} power.");
                _powerLoss = 0;
            }



        }
        public override void Kill()
        {
            if (!IsDead)
            {
                IsDead = true;
                BeamCannon.IsFiringLaserBeam = false;
                BeamCannon.LaserBeamTarget = null;
                RemoveDamageSentEntry();
                if (!ShipIsDead)
                {
                    Shooter.ProjectilesInFlight.Remove(this);
                }
                //Debug.Log($"{Name} has been killed and will be returned");
                Level.State.RemoveProjectile(this);
                Deactivate();
                Stage.Pool.ReturnProjectileToPool(this);
            }

        }
        //private Vector2 GetChangeInShooterPosition()
        //{
        //    Vector2 position = Shooter.GetPosition();
        //    Vector2 change = position - _lastShooterPosition;
        //    _lastShooterPosition = position;
        //    return change;
        //}
        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            ExtendBeam();
        }

        private float _worldAngle, _localAngle;
        private Vector2 _laserBeamOffset, _localCannonPoint, _rotatedLocalPosition, _offsetRotatedCannonPosition;
        private Quaternion _localRotation;
        private void ExtendBeam()
        {

            if (Shooter != null)
            {
                //Debug.Log($"Extending beam towards {_target.Name}");
                if (_target != null)
                {
                    _lastTargetPoint = _target.GetPosition();
                }
                //float distance = Weapon.DistanceToPoint(_lastTargetPoint);

                if (transform.localScale.x < Range / 2)
                {
                    //if (_scale == 0)
                    //{
                    //    transform.localScale = new Vector2(Weapon.DistanceToPoint(_lastTargetPoint) / 2, transform.localScale.y);
                    //}
                    transform.localScale = new Vector2(transform.localScale.x + _scale, transform.localScale.y);

                    _worldAngle = BeamCannon.GetRotation() + 90;
                    _localAngle = BeamCannon.GetLocalRotation() + 90;

                    _laserBeamOffset = new Vector2((transform.localScale.x), 0);

                    _localRotation = Quaternion.Euler(0, 0, _localAngle);

                    _localCannonPoint = Vector2.zero + _laserBeamOffset;
                    //Debug.Log($"Beamcannon local position: {Weapon.GetLocalPosition()}");
                    _rotatedLocalPosition = _localRotation * _localCannonPoint;
                    //Vector2 rotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition);
                    //Vector2 rotatedMapPosition = (Vector2)Level.Map.transform.TransformPoint(rotatedLocalPosition);
                    _offsetRotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(_rotatedLocalPosition) - Level.GetPosition();
                    Angle = _worldAngle * Mathf.Deg2Rad * -1;

                    //Debug.Log($"Cruiser world rotation: {Shooter.transform.eulerAngles.z}");
                    //Debug.Log($"Cannon local rotation: {Weapon.GetLocalRotation()}, world rotation: {Weapon.GetRotation()}");
                    //Debug.Log($"Beam world rotation: {worldAngle}");

                    //Debug.Log($"localCannonPoint #{Id}: {localCannonPoint}");
                    //Debug.Log($"rotatedLocalPosition #{Id}: {rotatedLocalPosition}");
                    //Debug.Log($"rotatedMapPosition #{Id}: {rotatedMapPosition}");

                    //Debug.Log($"offsetRotatedCannonPosition #{Id}: {offsetRotatedCannonPosition}");

                    //Debug.Log($"Extending Laser Beam #{Id} to rotated cannon position: {offsetRotatedCannonPosition}");

                    transform.localEulerAngles = new Vector3(0, 0, _worldAngle);
                    transform.localPosition = _offsetRotatedCannonPosition;
                    Body.velocity = Shooter.Body.velocity;
                    return;

                    //+ GetChangeInShooterPosition();
                }
                //else
                //{
                //    //Debug.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because distance ({distance}) > range ({Range})");
                //}
            }
            //else
            //{
            //    //Debug.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because the shooter is dead");
            //}
            RemoveDamageSentEntry();
            Kill();
        }
    }
}