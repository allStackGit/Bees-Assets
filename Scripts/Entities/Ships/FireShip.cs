using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Ships
{
    public class FireShip : Ship
    {
        public GameObject Explosion;
        public void Detonate()
        {
            //Debug.Log("Detonating fire ship");

            Kill(null);
        }
        public Weapon Bomb => Weapons.First();

        public override void Kill(Ship killer, bool endKill = false) // [kill-method] [damage-method] [note] [stats-method]
        {
            if (!IsDead)
            {
                IsDead = true;
                GameState state = Level.GetState();
                //Debug.Log("Fireship exploding");
                if (!endKill)
                {
                    Explosion = Instantiate(ShipExplosion, GetPosition(), Quaternion.identity);
                    Explosion.transform.parent = Level.Map.transform;
                    RocketExplosion explosion = (RocketExplosion)Explosion.GetComponent(typeof(RocketExplosion));
                    explosion.Setup(Level, Side, state.GetId(), Bomb, this, null, GetPosition(), 0, 0, Bomb.Power);
                    state.FireShipExplosions.Add(explosion);


                    int oldTsv = Tsv;
                    Health -= math.min(Bomb.Power, Health);

                    int tsvChange = Tsv - oldTsv; // this is a negative number since being hit by a projectile should induce a loss of TSV
                    
                    //Calling hit stats with a fire ship explosion. The shooter and shooter squad are null but the fire ship and fire ship squad are the target
                    LogHitStats(null, null, this, this.Squad, tsvChange);

                    if (Squad.Command != null)
                    {
                        Squad.Command.Tsv += tsvChange; // subtract the TSV from the squad
                    }

                    if (killer != null)
                    {
                        killer.LastKilled = Time.frameCount;
                        LogKillerStats(killer);
                    }
                    else
                    {
                        if (Level.ReplaceDeadShips && Squad.SavedSquad.HasBeenSavedToStorage)
                        {
                            FleetShip.IsDead = true;
                        }
                        Squad.SavedSquad.Stats.ShipsLost++;
                    }

                    if (HasVision)
                    {
                        Vision.Kill(3);
                    }
                }

                state.RemoveShip(this);
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
                if (!Level.IsTraining)
                {
                    Destroy(MovementMarker);
                }
                gameObject.SetActive(false);
                Invoke(nameof(DelayedKill), 8);

            }
           

        }
        private void DelayedKill()
        {
            Destroy(gameObject);
        }
    }


}