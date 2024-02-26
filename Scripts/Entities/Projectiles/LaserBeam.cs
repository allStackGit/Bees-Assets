

using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Security.Cryptography;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class LaserBeam : Projectile
    {

        private Vector2 _lastShooterPosition, _lastTargetPoint;
        private float _scale;
        private Ship _target;
        private int _powerLoss;
        void Start()
        {
            if (Weapon != null && Weapon.Ship != null & !Weapon.Ship.IsDead)
            {
                _scale = 1f;
                _lastShooterPosition = Weapon.GetPosition();
                _target = Weapon.TargetShip;
                _lastTargetPoint = _target.GetPosition();
                Angle = 0;
            }

        }
        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"{Name} hit {target.Name}");
            int halfHealth = target.Health / 2;
            _powerLoss = Mathf.Clamp(halfHealth, 0, Power);
            if (Power <= halfHealth)
            {
                Invoke(nameof(Kill), .5f);
                StopBeam();                
            }
            Invoke(nameof(DegradeBeam), 0f);


        }
        public void DegradeBeam()
        {
            Power -= _powerLoss;
            //Debug.Log($"{Name} lost {_powerLoss} power and now has {Power} power.");
            _powerLoss = 0;

        }
        public void StopBeam()
        {
            _scale = 0;
            Body.velocity = Vector2.zero;
        }
        public override void Kill()
        {
            //Debugger.Log($"killed projectile {name} #{Id}");
            BeamCannon weapon = (BeamCannon)Weapon;
            weapon.IsFiringLaserBeam = false;
            weapon.LaserBeamTarget = null;
            Level.GetState().RemoveProjectile(this);
            Destroy(gameObject);
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

            if (Shooter != null && !Shooter.IsDead)
            {
                //Debugger.Log($"Extending beam towards {_target.Name}");
                if (_target != null)
                {
                    _lastTargetPoint = _target.GetPosition();
                }
                float distance = Weapon.DistanceToPoint(_lastTargetPoint);

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

                    Vector2 localCannonPoint = Weapon.GetLocalPosition() + LaserBeamOffset;
                    Vector2 rotatedLocalPosition = localRotation * localCannonPoint;
                    //Vector2 rotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition);
                    //Vector2 rotatedMapPosition = (Vector2)Level.Map.transform.TransformPoint(rotatedLocalPosition);
                    Vector2 offsetRotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition) - Level.GetPosition();
                    Angle = worldAngle * Mathf.Deg2Rad * -1;

                    //Debugger.Log($"Cruiser world rotation: {Shooter.transform.eulerAngles.z}");
                    //Debugger.Log($"Cannon local rotation: {Weapon.GetLocalRotation()}, world rotation: {Weapon.GetRotation()}");
                    //Debugger.Log($"Beam world rotation: {worldAngle}");

                    //Debugger.Log($"localCannonPoint #{Id}: {localCannonPoint}");
                    //Debugger.Log($"rotatedLocalPosition #{Id}: {rotatedLocalPosition}");
                    //Debugger.Log($"rotatedMapPosition #{Id}: {rotatedMapPosition}");

                    //Debugger.Log($"offsetRotatedCannonPosition #{Id}: {offsetRotatedCannonPosition}");

                    //Debugger.Log($"Extending Laser Beam #{Id} to rotated cannon position: {offsetRotatedCannonPosition}");

                    transform.localPosition = offsetRotatedCannonPosition;
                    Body.velocity = Shooter.Velocity;
                    return;

                    //+ GetChangeInShooterPosition();
                }
                else
                {
                    //Debugger.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because distance ({distance}) > range ({Range})");
                }
            }
            else
            {
                //Debugger.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because the shooter is dead");
            }
            RemoveDamageSentEntry();
            Kill();
        }
    }
}