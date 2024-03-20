

using System.Collections;
using UnityEngine;
using Assets.Scripts.Entities.Ships.Weapons;


namespace Assets.Scripts.Entities.Ships
{
    public class LaserBuilder : Turret
    {
        private bool _readyForFiring;
        public SpriteRenderer Pupil;
        public LaserBuilderControl LaserBuilderControl;
        public GameObject LaserBuilderAnimation;

        public override void Setup(Ship ship, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Setup(ship, range, power, rateOfFire, projectileValue, piece, projectilePrefab, fireAtFrontOfShip, rotationRate);
            LaserBuilderAnimation = Piece.transform.Find("Laser Animation").gameObject;
            LaserBuilderControl = LaserBuilderAnimation.GetComponent<LaserBuilderControl>();
            LaserBuilderControl.Setup(this);
        }
        protected override void SendProjectile() // [projectile-method] [note] this doesn't actually send the projectile because we need to wait for the animation to finish
        {
            _readyForFiring = true;
            //Debug.Log($"{Name} send projectile called");

        }
        public void ActuallyShoot() // [projectile-method] [note] this actually sends the projectile once the animation is finished
        {
            if (_readyForFiring && (HasTargetShip || IsFiringManually))
            {
                //Debug.Log($"{Name} animation finished, sending projectile, deactivating animation");
                base.SendProjectile();
                LaserBuilderAnimation.SetActive(false);
            }

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debug.Log($"Setting {Name} target, activating animation");
            base.SetTargetShip(ship);
            LaserBuilderAnimation.SetActive(true);



        }
        protected override void Aim()
        {
            //Debug.Log("Leafcutter aiming");
            base.Aim();
            if ((!HasTargetShip || CeaseFire) && !IsFiringManually)
            {
                //Debug.Log($"{Name} has no TargetShip, deactivating animation");
                LaserBuilderAnimation.SetActive(false);
            }
        }
    }
}