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
        private ScaledTimer _killSequenceTimer = new ScaledTimer();
        private ScaledTimer _damageTimer = new ScaledTimer();
        // Use this for initialization

        public void Setup(Level level, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power, Ship contactedShip)
        {
            base.Setup(level, weapon, shooter, target, startingPosition, angle, range, power);
            ContactedShip = contactedShip;
            transform.parent = ContactedShip.transform;
            _killSequenceTimer.Reuse(1.5f, KillSequence);
            Level.AddTimer(_killSequenceTimer);
            //Invoke(nameof(KillSequence), 1.5f);
        }


        public override void KillSequence()
        {
            if (!ContactedShip.IsDead)
            {
                if (!Level.Stage.IsTraining)
                {
                    Explosion.transform.parent = ContactedShip.transform;
                    Explosion.transform.localPosition = GetPosition();
                    Explosion.SetActive(true);
                }

                _damageTimer.Reuse(.5f, Damage);
                Level.AddTimer(_damageTimer);
                //Invoke(nameof(Damage), .5f);
                Kill();
            }
            else
            {
                Kill();
            }
        }
        public override void Kill()
        {
            Level.CancelTimer(_killSequenceTimer);
            base.Kill();
        }
        public void Damage()
        {
            if (!ContactedShip.IsDead)
            {
                Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ContactedShip);
            }
        }
        protected override void FixedUpdate()
        {

        }
    }
}