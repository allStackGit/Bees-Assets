using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class FireBarge : Ship
    {
        private RocketExplosion Explosion;
        private AudioSource ExplosionSound;
        public Bomb Bomb;
        private readonly ScaledTimer _delayedKillTimer = new ScaledTimer();
        private bool _waitingForDelayedRelease;

        public override void Create(Stage stage)
        {
            base.Create(stage);
            Bomb = (Bomb)Weapons.First();
            IsBomber = true;
            Destroy(Bomb.Piece);
        }

        public override void ClearData()
        {
            if (Level != null)
            {
                Level.CancelTimer(_delayedKillTimer);
            }
            _waitingForDelayedRelease = false;
            base.ClearData();
        }

        public void Detonate()
        {
            Kill(null, null, null);
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (IsDead)
            {
                return;
            }

            Bomb.ReleaseTargetReservation();
            StopMoving();
            CannotChangeMovementOrders = true;
            IsDead = true;

            if (!endKill)
            {
                Explosion = (RocketExplosion)Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.FireBargeExplosion);
                ExplosionSound = Explosion.GetComponent<AudioSource>();
                ShipExplosion = Explosion.gameObject;
                DropExplosionAnimation();
                if (Level.Stage.ActivateAudio)
                {
                    ExplosionSound.Play();
                }
                Explosion.Setup(Level, Bomb, this, null, GetPosition(), 0, 0, Bomb.Power);
                Level.State.FireBargeExplosions.Add(Explosion);
                ProjectilesInFlight.Add(Explosion);

                // The Fire Barge is killing itself so it takes full damage, but there is
                // no external shooter for this part of the damage accounting.
                LogDamage(Health);

                if (killer != null)
                {
                    killer.LastKilled = Time.frameCount;
                    Killer = killer;
                    KillerFleetShip = killer.FleetShip;
                    KillerSavedSquad = killer.Squad.SavedSquad;
                    LogKillerStats(KillerFleetShip, KillerSavedSquad);
                }

                if (HasUserFogOfWarVision)
                {
                    FogOfWarVision.Kill(3, false);
                }

                if (WeaponsThatHaveUsWithinRange.Count > 0)
                {
                    List<Weapon> weapons = WeaponsThatHaveUsWithinRange.ToList();
                    for (int i = 0; i < weapons.Count; i++)
                    {
                        weapons[i].ShipsWithinRange.Remove(Id);
                    }
                    WeaponsThatHaveUsWithinRange.Clear();
                }

                // Own all common death accounting here. Do not also increment ShipsLost or
                // set FleetShip.IsDead in this special path; LogKilledStats already does it.
                LogKilledStats();
            }

            Level.State.RemoveShip(this);
            Squad.RemoveShip(this);

            if (Squad.GetShips().Count == 0)
            {
                Squad.Kill(endKill);
            }
            else
            {
                Squad.SetOffsets();
            }

            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = false;
            }
            if (!Stage.IsTraining)
            {
                if (HasRocketFlares)
                {
                    RightRocketFlares.ForEach(flare => flare.SetActive(false));
                    LeftRocketFlares.ForEach(flare => flare.SetActive(false));
                }
                HealthBar.SetActive(false);

                if (!endKill)
                {
                    // Keep the dead wrapper in GameState.ShipsToRelease so level teardown
                    // cannot lose ownership while this presentation delay is pending.
                    _waitingForDelayedRelease = true;
                    _delayedKillTimer.Reuse(5f, DelayedKill);
                    Level.AddTimer(_delayedKillTimer);
                }
                else
                {
                    Deactivate();
                }
            }
            else
            {
                Deactivate();
            }
        }

        public override bool CanReturnToPool()
        {
            return !_waitingForDelayedRelease && base.CanReturnToPool();
        }

        public void PrepareForLevelTeardown()
        {
            if (!_waitingForDelayedRelease)
            {
                return;
            }

            Level.CancelTimer(_delayedKillTimer);
            _waitingForDelayedRelease = false;
            Deactivate();
        }

        protected void DelayedKill()
        {
            Level.CancelTimer(_delayedKillTimer);
            Deactivate();
            _waitingForDelayedRelease = false;
        }
    }
}
