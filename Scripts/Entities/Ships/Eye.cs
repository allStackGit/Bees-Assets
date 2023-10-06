

using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Assets.Scripts.Entities.Ships
{
    public class Eye : Turret
    {
        private bool _readyToChangeColor;
        public SpriteRenderer Pupil;

        public override void Setup(Ship ship, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip)
        {
            base.Setup(ship, range, power, rateOfFire, projectileValue, piece, projectilePrefab, fireAtFrontOfShip);
            Pupil = Piece.gameObject.transform.GetChild(0).GetComponent<SpriteRenderer>();
        }
        protected override void SendProjectile() // [projectile-method] [note]
        {
            base.SendProjectile();
            //reset the targeting eye color
            Pupil.color = Color.white;
            _readyToChangeColor = false;
            //Debugger.Log("Sending projectile, Setting color white!");

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debugger.Log("Setting eye targeting");
            base.SetTargetShip(ship);
            if (!_readyToChangeColor)
            {
                //Debugger.Log($"Got target! {ship.name}");
                _readyToChangeColor = true;
            }
            else
            {
                //Debugger.Log("Setting color red!");
                Pupil.color = ConfigData.GetUIColor("eye-aiming");
            }
        }
        protected override void Aim()
        {
            //Debugger.Log("Hornet aiming");
            base.Aim();
            // resets the eye color if there is no target
            if (!HasTargetShip)
            {
                //Debugger.Log("Setting color white!");
                Pupil.color = Color.white;
            }
        }
    }
}