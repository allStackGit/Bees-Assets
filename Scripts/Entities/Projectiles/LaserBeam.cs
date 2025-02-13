

using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Security.Cryptography;
using static UnityEngine.GraphicsBuffer;
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
        public override void Setup(Level level, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            base.Setup(level, weapon, shooter, target, startingPosition, angle, range, power);
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
            transform.localScale = new Vector2(1, .5f);
            _shipsHit.Clear();
            Angle = 0;
        }
        public override void ContactTarget(Ship target)
        {
            if (!_shipsHit.Contains(target))
            {
                _shipsHit.Add(target);
                //Debug.Log($"{Name} hit {target.Name}");
                int halfHealth = target.Health / 2;
                _powerLoss = Mathf.Clamp(halfHealth, 0, Power);
                if (Power <= halfHealth)
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
                BeamCannon weapon = (BeamCannon)Weapon;
                weapon.IsFiringLaserBeam = false;
                weapon.LaserBeamTarget = null;
                RemoveDamageSentEntry();
                if (!ShipIsDead)
                {
                    Shooter.ProjectilesInFlight.Remove(this);
                }
                //Debug.Log($"{Name} has been killed and will be returned");
                Stage.Pool.ReturnProjectileToPool(this);
            }

        }
        private Vector2 GetChangeInShooterPosition()
        {
            Vector2 position = Shooter.GetPosition();
            Vector2 change = position - _lastShooterPosition;
            _lastShooterPosition = position;
            return change;
        }
        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            ExtendBeam();
        }

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

                    float worldAngle = Weapon.GetRotation() + 90;
                    float localAngle = Weapon.GetLocalRotation() + 90;

                    Vector2 LaserBeamOffset = new Vector2((transform.localScale.x), 0);

                    Quaternion localRotation = Quaternion.Euler(0, 0, localAngle);

                    Vector2 localCannonPoint = Vector2.zero + LaserBeamOffset;
                    //Debug.Log($"Beamcannon local position: {Weapon.GetLocalPosition()}");
                    Vector2 rotatedLocalPosition = localRotation * localCannonPoint;
                    //Vector2 rotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition);
                    //Vector2 rotatedMapPosition = (Vector2)Level.Map.transform.TransformPoint(rotatedLocalPosition);
                    Vector2 offsetRotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition) - Level.GetPosition();
                    Angle = worldAngle * Mathf.Deg2Rad * -1;

                    //Debug.Log($"Cruiser world rotation: {Shooter.transform.eulerAngles.z}");
                    //Debug.Log($"Cannon local rotation: {Weapon.GetLocalRotation()}, world rotation: {Weapon.GetRotation()}");
                    //Debug.Log($"Beam world rotation: {worldAngle}");

                    //Debug.Log($"localCannonPoint #{Id}: {localCannonPoint}");
                    //Debug.Log($"rotatedLocalPosition #{Id}: {rotatedLocalPosition}");
                    //Debug.Log($"rotatedMapPosition #{Id}: {rotatedMapPosition}");

                    //Debug.Log($"offsetRotatedCannonPosition #{Id}: {offsetRotatedCannonPosition}");

                    //Debug.Log($"Extending Laser Beam #{Id} to rotated cannon position: {offsetRotatedCannonPosition}");

                    transform.localEulerAngles = new Vector3(0, 0, worldAngle);
                    transform.localPosition = offsetRotatedCannonPosition;
                    Body.velocity = Shooter.Body.velocity;
                    return;

                    //+ GetChangeInShooterPosition();
                }
                else
                {
                    //Debug.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because distance ({distance}) > range ({Range})");
                }
            }
            else
            {
                //Debug.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because the shooter is dead");
            }
            RemoveDamageSentEntry();
            Kill();
        }
    }
}