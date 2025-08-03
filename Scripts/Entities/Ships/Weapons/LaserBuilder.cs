

using System.Collections;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;


namespace Assets.Scripts.Entities.Ships
{
    public class LaserBuilder : Turret
    {
        /// <summary>
        /// The turret is ready to fire when the animation is finished.
        /// </summary>
        //public bool IsReadyForFiring;
        public SpriteRenderer Pupil;
        public LaserBuilderControl LaserBuilderControl;
        public GameObject LaserBuilderAnimation;
        public Animator Animator;

        public override void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            WeaponsData weaponsData = piece.GetComponent<WeaponsData>();
            Animator = weaponsData.Animator;

            base.Create(ship, type, weaponSound, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
            LaserBuilderAnimation = PieceTransform.Find("Laser Animation").gameObject;
            LaserBuilderControl = LaserBuilderAnimation.GetComponent<LaserBuilderControl>();
            LaserBuilderControl.Setup(this);
        }
        public override void ClearData()
        {
            base.ClearData();
            //IsReadyForFiring = false;
        }
        public override void Activate()
        {
            base.Activate();
            Animator.enabled = true;
        }
        public override void Deactivate()
        {
            base.Deactivate();
            Animator.enabled = false;
        }
        protected override void SendProjectile() // [projectile-method] [note] this doesn't actually send the projectile because we need to wait for the animation to finish
        {
            //IsReadyForFiring = true;
            //Debug.Log($"{Name} send projectile called");

        }
        public void ActuallyShoot() // [projectile-method] [note] this actually sends the projectile once the animation is finished
        {
            //if (IsReadyForFiring && (HasTargetShip || IsFiringManually || HasTargetAsteroid) && IsAimedAtTarget)
            //{
            //    //Debug.Log($"{Name} animation finished, sending projectile, deactivating animation");
            //    base.SendProjectile();
            //    LaserBuilderAnimation.SetActive(false);
            //}
            base.SendProjectile();
            LaserBuilderAnimation.SetActive(false);

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debug.Log($"{Name} set target ship to {ship}");
            base.SetTargetShip(ship);
            LaserBuilderAnimation.SetActive(true);
        }
        protected override void Aim()
        {
            //Debug.Log("Leafcutter aiming");
            //base.Aim();

            if (IsFiringManually)
            {
                TargetPoint = Stage.InputManager.GetMousePosition();
                IsAimedAtTarget = true;
            }
            else
            {
                if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                    IsAimedAtTarget = true;
                    IsFiringAtAsteroid = false;
                }
                else if (ShouldFireAtAsteroid)
                {
                    TargetPoint = TargetAsteroid.GetPosition();
                    IsAimedAtTarget = true;
                    IsFiringAtAsteroid = true;
                }
                else
                {
                    IsAimedAtTarget = false;
                    IsFiringAtAsteroid = false;
                }
            }
            MoveTargetingMarker();

            if ((IsFiringManually && IsAimedAtTarget) || (IsFiringAtAsteroid && IsAimedAtTarget))
            {
                LaserBuilderAnimation.SetActive(true);
            }
            else
            {
                if (LaserBuilderAnimation.activeSelf && (!HasTargetShip || Ship.IsCeaseFire || !IsAimedAtTarget))
                {
                    //Debug.Log($"{Name} has no TargetShip, deactivating animation");
                    LaserBuilderAnimation.SetActive(false);
                }
            }

        }
    }
}