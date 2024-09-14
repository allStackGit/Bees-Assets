

using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Eye : Turret
    {
        private bool _readyToChangeColor;
        public SpriteRenderer Pupil;

        public override void Setup(Ship ship, string type, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Setup(ship, type, range, power, rateOfFire, projectileValue, piece, projectilePrefab, fireAtFrontOfShip, rotationRate);
            Pupil = Piece.gameObject.transform.GetChild(0).GetComponent<SpriteRenderer>();
        }
        protected override void SendProjectile() // [projectile-method] [note]
        {
            base.SendProjectile();
            //reset the targeting eye color
            Pupil.color = Color.white;
            _readyToChangeColor = false;
            //Debug.Log("Sending projectile, Setting color white!");

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debug.Log("Setting eye targeting");
            base.SetTargetShip(ship);
            if (!_readyToChangeColor)
            {
                //Debug.Log($"Got target! {ship.name}");
                _readyToChangeColor = true;
            }
            else if (!CeaseFire)
            {
                //Debug.Log("Setting color red!");
                Pupil.color = ConfigData.GetUIColor("eye-aiming");
            }

        }
        protected override void Aim()
        {
            //Debug.Log("Hornet aiming");
            base.Aim();
            // resets the eye color if there is no target
            if ((!HasTargetShip && !IsFiringManually) || CeaseFire || !IsAimedAtTarget)
            {
                //Debug.Log("Setting color white!");
                Pupil.color = Color.white;
            }else if (IsFiringManually)
            {
                if (!_readyToChangeColor)
                {
                    //Debug.Log($"Got target! {ship.name}");
                    _readyToChangeColor = true;
                }
                else
                {
                    //Debug.Log("Setting color red!");
                    Pupil.color = ConfigData.GetUIColor("eye-aiming");
                }
            }
        }
    }
}