

using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Assets.Scripts.Entities.Ships
{
    public class LaserBuilder : Turret
    {
        private bool _readyForFiring;
        public SpriteRenderer Pupil;
        public LaserBuilderControl LaserBuilderControl;

        public override void Setup(Ship ship, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip)
        {
            base.Setup(ship, range, power, rateOfFire, projectileValue, piece, projectilePrefab, fireAtFrontOfShip);
            LaserBuilderControl = Piece.GetComponent<LaserBuilderControl>();
            LaserBuilderControl.Setup(this);
        }
        protected override void SendProjectile() // [projectile-method] [note] this doesn't actually send the projectile because we need to wait for the animation to finish
        {
            _readyForFiring = true;
            //Debugger.Log("Send projectile called");

        }
        public void ActuallyShoot() // [projectile-method] [note] this actually sends the projectile once the animation is finished
        {
            if (_readyForFiring && HasTargetShip)
            {
                //Debugger.Log("Animation finished, sending projectile");
                base.SendProjectile();
                Piece.SetActive(false);
            }

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debugger.Log("Setting laser builder targeting");
            base.SetTargetShip(ship);
            Piece.SetActive(true);


        }
        protected override void Aim()
        {
            //Debugger.Log("Leafcutter aiming");
            base.Aim();
            // resets the eye color if there is no target
            if (!HasTargetShip)
            {
                Piece.SetActive(false);
            }
        }
    }
}