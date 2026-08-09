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
        public Weapon Bomb;
        private ScaledTimer _delayedKillTimer = new ScaledTimer();
        private bool _waitingForDelayedRelease;

        public override void Create(Stage stage)
        {
            base.Create(stage);
            Bomb = Weapons.First();
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
                if (Level.Stage.ReplaceDeadShips && Squad.SavedSquad.HasBeenSavedToStorage)
                {
                    FleetShip.IsDead = true;
                }
                Squad.SavedSquad.Stats.ShipsLost++;

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
                    // RemoveShip normally makes the wrapper immediately poolable. Keep
                    // this dead Fire Barge reserved until the explosion's five-second
                    // lifetime has completed so delayed callbacks/projectiles cannot see
                    // a newly configured occupant of the same pooled Ship object.
                    Level.State.ShipsToRelease.Remove(this);
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

        protected void DelayedKill()
        {
            Level.CancelTimer(_delayedKillTimer);
            Deactivate();
            if (_waitingForDelayedRelease)
            {
                _waitingForDelayedRelease = false;
                if (!Level.State.ShipsToRelease.Contains(this))
                {
                    Level.State.ShipsToRelease.Add(this);
                }
            }
        }
    }
}
