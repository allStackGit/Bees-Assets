

using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// The eye is a bee specific turret that turns red when it is aimed at a target and ready to fire.
    /// </summary>
    public class Eye : Turret
    {
        private bool _readyToChangeColor;
        public SpriteRenderer Pupil;

        public override void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            base.Create(ship, type, weaponSound, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
            Pupil = Piece.gameObject.transform.GetChild(0).GetComponent<SpriteRenderer>();
            if (!Stage.IsRendering)
            {
                Destroy(Pupil);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            _readyToChangeColor = false;
        }
        protected override void SendProjectile() // [projectile-method] [note]
        {
            base.SendProjectile();
            //reset the targeting eye color
            if (Stage.IsRendering)
            {
                Pupil.color = Color.white;
                _readyToChangeColor = false;
            }

            //Debug.Log("Sending projectile, Setting color white!");

        }
        protected override void SetTargetShip(Ship ship)
        {
            //Debug.Log("Setting eye targeting");
            base.SetTargetShip(ship);
            if (Stage.IsRendering)
            {
                if (!_readyToChangeColor)
                {
                    //Debug.Log($"Got target! {ship.Name}");
                    _readyToChangeColor = true;
                }
                else if (!Ship.IsCeaseFire && IsAimedAtTarget)
                {
                    //Debug.Log("Setting color red!");
                    Pupil.color = ConfigData.GetUIColor("eye-aiming");
                }
            }


        }
        protected override void Aim()
        {
            //Debug.Log("Hornet aiming");
            base.Aim();
            // resets the eye color if there is no target
            if (Stage.IsRendering)
            {
                if (((!HasTargetShip && !IsFiringManually) || Ship.IsCeaseFire || !IsAimedAtTarget) && Pupil.color != Color.white)
                {
                    //Debug.Log("Setting color white!");
                    //Debug.Log($"!HasTargetShip: {!HasTargetShip}");
                    //Debug.Log($"!IsFiringManually: {!IsFiringManually}");
                    //Debug.Log($"Ship.IsCeaseFire: {Ship.IsCeaseFire}");
                    //Debug.Log($"!IsAimedAtTarget: {!IsAimedAtTarget}");
                    //Debug.Log($"Pupil.color: {Pupil.color}");

                    Pupil.color = Color.white;
                }
                else if (IsFiringManually)
                {
                    if (!_readyToChangeColor)
                    {
                        //Debug.Log($"Got target!");
                        _readyToChangeColor = true;
                    }
                    else if (IsAimedAtTarget)
                    {
                        //Debug.Log("Setting color red!");
                        Pupil.color = ConfigData.GetUIColor("eye-aiming");
                    }
                    //else
                    //{
                    //    Debug.Log($"Firing manually but not yet aimed at target.");
                    //}
                }
            }

        }
    }
}