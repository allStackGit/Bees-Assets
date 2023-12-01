

using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class LaserBeam : Projectile
    {

        private Vector2 _lastShooterPosition;
        private float _scale;
        private Ship _target;
        void Start()
        {
            if (Weapon != null && Weapon.Ship != null & !Weapon.Ship.IsDead)
            {
                _scale = 1f;
                _lastShooterPosition = Weapon.GetPosition();
                _target = Weapon.TargetShip;
                Angle = 0;
            }

        }
        public override void ContactTarget(Ship target)
        {
            //Debugger.Log($"Projectile hit {target.name}");
            Invoke(nameof(Kill), .5f);
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
        void FixedUpdate()
        {
            base.FixedUpdate();
            ExtendBeam();
        }

        private void ExtendBeam()
        {

            if (_target != null && !_target.IsDead)
            {
                if (Shooter != null && !Shooter.IsDead)
                {
                    //Debugger.Log($"Extending beam towards {_target.Name}");

                    float distance = Weapon.DistanceTo(_target);

                    if (transform.localScale.x < Range / 2 && distance < Range)
                    {
                        if (_scale == 0)
                        {
                            transform.localScale = new Vector2(Weapon.DistanceTo(_target)/2, transform.localScale.y);
                        }
                        transform.localScale = new Vector2(transform.localScale.x + _scale, transform.localScale.y);

                        float worldAngle = Weapon.GetRotation()+90;
                        float localAngle = Weapon.GetLocalRotation()+90;

                        Vector2 LaserBeamOffset = new Vector2((transform.localScale.x), 0);

                        Quaternion localRotation = Quaternion.Euler(0, 0, localAngle);

                        Vector2 localCannonPoint = Weapon.GetLocalPosition() + LaserBeamOffset;
                        Vector2 rotatedLocalPosition = localRotation * localCannonPoint;
                        //Vector2 rotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition);
                        //Vector2 rotatedMapPosition = (Vector2)Level.Map.transform.TransformPoint(rotatedLocalPosition);
                        Vector2 offsetRotatedCannonPosition = (Vector2)Shooter.transform.TransformPoint(rotatedLocalPosition) - (Vector2) Level.transform.position;
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
            }
            else
            {
                //Debugger.Log($"Killing {gameObject.name} at position ({transform.localPosition}) because the target is dead");
            }
            RemoveDamageSentEntry();
            Kill();
        }
    }
}