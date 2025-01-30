using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class StrikerBomb : Projectile
    {
        public Ship ContactedShip;
        // Use this for initialization
        public void Setup(Level level, long id, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power, Ship contactedShip)
        {
            base.Setup(level, id, weapon, shooter, target, startingPosition, angle, range, power);
            ContactedShip = contactedShip;
            transform.parent = ContactedShip.transform;
            Invoke(nameof(KillSequence), 1.5f);
        }


        public override void KillSequence()
        {
            if (!ContactedShip.IsDead)
            {
                Explosion.transform.parent = ContactedShip.transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.SetActive(true);
                Kill();
                Invoke(nameof(Damage), .5f);
            }
        }
        public void Damage()
        {
            Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ContactedShip);
        }
    }
}