using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class QueenEye : Eye
    {
        SpriteRenderer InnerEye;
        public override void Create(Ship ship, ConfigData.WeaponTypes type, ConfigData.WeaponSoundTypes weaponSound, int range, int power, float rateOfFire, float projectileValue, GameObject piece, ConfigData.ProjectileTypes projectileType, bool fireAtFrontOfShip, float rotationRate)
        {
            WeaponsData weaponsData = piece.GetComponent<WeaponsData>();
            InnerEye = weaponsData.SecondSpriteRenderer;
            if (!ship.Stage.IsRendering)
            {
                Destroy(InnerEye);
            }
            base.Create(ship, type, weaponSound, range, power, rateOfFire, projectileValue, piece, projectileType, fireAtFrontOfShip, rotationRate);
        }
        public override void Activate()
        {
            base.Activate();
            if (Stage.IsRendering)
            {
                InnerEye.enabled = true;
            }
        }

        public override void Deactivate() { 
            base.Deactivate(); 

            if (Stage.IsRendering)
            {
                InnerEye.enabled = false;
            }
        }
    }
}