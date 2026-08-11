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
        public AudioSource BombReleaseSound;
        public AudioSource BombExplosionSound;
        private long _shooterFleetShipId;
        private long _contactedShipRuntimeId;

        public void Setup(Level level, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power, Ship contactedShip)
        {
            base.Setup(level, weapon, shooter, target, startingPosition, angle, range, power);
            ContactedShip = contactedShip;
            _shooterFleetShipId = FleetShip.Id;
            _contactedShipRuntimeId = contactedShip.Id;
            transform.parent = ContactedShip.transform;
            _killSequenceTimer.Reuse(1.5f, KillSequence);
            Level.AddTimer(_killSequenceTimer);
            if (Stage.ActivateAudio)
            {
                BombReleaseSound.Play();
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            ContactedShip = null;
            _shooterFleetShipId = 0;
            _contactedShipRuntimeId = 0;
        }

        private bool HasOriginalContactedShip()
        {
            return ContactedShip != null && !ContactedShip.IsDead && ContactedShip.Id == _contactedShipRuntimeId;
        }

        public override void KillSequence()
        {
            Level.CancelTimer(_killSequenceTimer);
            if (HasOriginalContactedShip())
            {
                if (!Level.Stage.IsTraining)
                {
                    Explosion.transform.parent = ContactedShip.transform;
                    Explosion.transform.localPosition = GetPosition();
                    Explosion.SetActive(true);
                    if (Stage.ActivateAudio)
                    {
                        BombExplosionSound.Play();
                    }
                }

                // Keep this projectile out of the pool until its delayed damage has
                // resolved. Returning it here allowed Setup on a new shot to overwrite
                // ContactedShip/Power/Shooter before Damage ran.
                Deactivate();
                _damageTimer.Reuse(.5f, DamageAndKill);
                Level.AddTimer(_damageTimer);
            }
            else
            {
                Kill();
            }
        }

        public override void Kill()
        {
            Level.CancelTimer(_killSequenceTimer);
            Level.CancelTimer(_damageTimer);
            base.Kill();
        }

        private void DamageAndKill()
        {
            Damage();
            Kill();
        }

        public void Damage()
        {
            if (!HasOriginalContactedShip())
            {
                return;
            }

            // Ship wrappers are pooled independently of projectiles. If the original
            // Striker has already been recycled, preserve the explosion damage without
            // attributing it to the new occupant of that pooled Ship object.
            if (Shooter != null && Shooter.FleetShip != null && Shooter.FleetShip.Id == _shooterFleetShipId)
            {
                Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ContactedShip, CommandOutcomeId);
            }
            else
            {
                ContactedShip.LogDamage(Power);
            }
        }
        protected override void FixedUpdate()
        {
            if (!HasOriginalContactedShip())
            {
                Kill();
            }
        }
    }
}
