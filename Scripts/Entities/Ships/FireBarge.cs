using Assets.Scripts.Data;
using Assets.Scripts.Entities;
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
        RocketExplosion Explosion;
        public void Detonate()
        {
            //Debug.Log("Detonating Fire Barge");

            Kill(null, null, null);
        }
        public override void Create(Stage stage)
        {
            base.Create(stage);
            Explosion = (RocketExplosion)ShipExplosion.GetComponent(typeof(RocketExplosion));
        }
        public Weapon Bomb => Weapons.First();

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false) // [kill-method] [damage-method] [note] [stats-method]
        {
            if (!IsDead)
            {
                IsDead = true;
                //Debug.Log("FireBarge exploding");
                if (!endKill)
                {
                    DropExplosionAnimation();
                    Explosion.Setup(Level, Level.State.GetId(), Bomb, this, null, GetPosition(), 0, 0, Bomb.Power);
                    Level.State.FireBargeExplosions.Add(Explosion);
                    ProjectilesInFlight.Add(Explosion);


                    // The Fire Barge is killing itself so it takes full damage but there's no shooter so it's just logging damage
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

                    if (HasVision)
                    {
                        Vision.Kill(3);
                    }

                    if (WeaponsThatHaveUsWithinRange.Count > 0)
                    {
                        List<Weapon> weapons = WeaponsThatHaveUsWithinRange.ToList();
                        for (int i = 0; i < WeaponsThatHaveUsWithinRange.Count; i++)
                        {
                            weapons[i].ShipsWithinRange.Remove(this);
                        }
                    }
                }

                Level.State.RemoveShip(this);
                Squad.RemoveShip(this);

                if (Squad.GetShips().Count == 0)
                {
                    //Squad.SavedSquad.Stats.BattlesFought++;
                    Squad.Kill(endKill);
                }
                else
                {
                    Squad.SetOffsets();
                }
                //if (!Stage.IsTraining)
                //{
                //    Destroy(MovementMarker);
                //}
                gameObject.SetActive(false);
                Invoke(nameof(DelayedKill), 5);

            }
           

        }
    }


}