
using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class DualCannon : Turret
    {

        public Vector2 LeftCannonOffset = new Vector2(-1.1f, .5f);
        public Vector2 RightCannonOffset = new Vector2(1.1f, .5f);
        public SpriteRenderer SecondCannonSpriteRenderer;
        //public GameObject RangeCircleLeft; // For the second cannon in the dual cannon, the first one being the right one
        //public CircleCollider2D RangeColliderLeft; // Likewise for the range collider
        public override void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            WeaponsData weaponsData = piece.GetComponent<WeaponsData>();
            SecondCannonSpriteRenderer = weaponsData.SecondSpriteRenderer;
            if (!ship.Stage.IsRendering)
            {
                Destroy(SecondCannonSpriteRenderer);
            }
            base.Create(ship, type, weaponSound, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
        }
        private float _angle, _cannonAngle;
        private Vector2 _position, _shipPosition, _leftCannonPosition, _rightCannonPosition, _rotatedLeftCannonPosition, _rotatedRightCannonPosition;
        protected override void SendProjectile() // [projectile-method] [note] [stats-method]
        {

            //get the angle to the target ship
            _angle = Ship.AngleToPoint(TargetPoint);

            // get the targetShipPosition of the cannons
            _shipPosition = Ship.GetPosition();
            _position = GetPosition();
            _leftCannonPosition = _position + LeftCannonOffset;
            _rightCannonPosition = _position + RightCannonOffset;

            // get the angle of the cannons
            _cannonAngle = Rotation * Mathf.Deg2Rad;

            // calculate the rotated targetShipPosition of the end of the cannons
            _rotatedLeftCannonPosition = Utilities.RotatePointAroundPoint(_shipPosition, _leftCannonPosition, _cannonAngle);
            _rotatedRightCannonPosition = Utilities.RotatePointAroundPoint(_shipPosition, _rightCannonPosition, _cannonAngle);

            if (IsFiringManually || IsFiringAtAsteroid)
            {
                SetTargetShipNull();
            }

            // instantiate the projectiles
            Level.AddProjectile(ConfigData.ProjectileTypes.HumanSmall, this, _rotatedLeftCannonPosition, _angle);
            Level.AddProjectile(ConfigData.ProjectileTypes.HumanSmall, this, _rotatedRightCannonPosition, _angle);
            Ship.FleetShip.ShotsFired += 2;

            // Level.AddProjectile halves DualCannon projectile power and this method
            // fires two shots, so their aggregate reserved damage is exactly Power.
            if (!IsFiringManually && !IsFiringAtAsteroid)
            {
                Level.State.GetShipDamageStatus(Side, TargetShip).TotalDamageSentToShip += Power;
            }
            PlaySoundEffect();

        }
        public override void Activate()
        {
            base.Activate();
            if (Stage.IsRendering)
            {
                SecondCannonSpriteRenderer.enabled = true;
            }
        }
        public override void Deactivate()
        {
            base.Deactivate();
            if (Stage.IsRendering)
            {
                SecondCannonSpriteRenderer.enabled = false;
            }
        }

        // UI Methods
        public override void ShowRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(true);
                //RangeCircleLeft.SetActive(true);
            }
        }

        public override void HideRange()
        {
            if (HasRangeCircle)
            {
                RangeCircle.SetActive(false);
                //RangeCircleLeft.SetActive(false);
            }
        }

    }
}