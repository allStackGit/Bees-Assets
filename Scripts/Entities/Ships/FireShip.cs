using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Ships
{
    public class FireShip : Ship
    {
        public GameObject Explosion;
        public void Detonate()
        {
            //Debugger.Log("Detonating fire ship");

            Kill(null);
        }
        public Weapon Bomb => Weapons.First();

        public override void Kill(Ship killer, bool endKill = false) // [kill-method] [damage-method] [note] [stats-method]
        {
            if (!IsDead)
            {
                IsDead = true;
                GameState state = Level.GetState();
                //Debugger.Log("Fireship exploding");
                if (!endKill)
                {
                    Explosion = Instantiate(ShipExplosion, GetPosition(), Quaternion.identity);
                    Explosion.transform.parent = Level.Map.transform;
                    RocketExplosion explosion = (RocketExplosion)Explosion.GetComponent(typeof(RocketExplosion));
                    explosion.Setup(Level, Side, state.AddEntity(), Bomb, this, null, GetPosition(), 0, 0, Bomb.Power);
                    state.AddExplosion(explosion);


                    int oldTsv = Tsv;
                    Health -= Bomb.Power;

                    if (Health < 0)
                    {
                        Health = 0;
                    }

                    int tsvChange = Tsv - oldTsv; // this is a negative number since being hit by a projectile should induce a loss of TSV
                    LogHitStats(null, null, this, this.Squad, tsvChange, true);

                    if (Squad.Command != null)
                    {
                        Squad.Command.Tsv += tsvChange; // subtract the TSV from the squad
                    }

                    if (killer != null)
                    {
                        killer.LastKilled = state.Ticks;
                        LogKillStats(killer);
                    }
                    else
                    {
                        if (Level.ReplaceDeadShips && Squad.SavedSquad.HasBeenSavedToStorage)
                        {
                            FleetShip.IsDead = true;
                        }
                        Squad.SavedSquad.Stats.ShipsLost++;
                    }
                }


                state.RemoveShip(this);
                Squad.RemoveShip(this);

                if (Squad.GetShips().Count <= 0)
                {
                    //Squad.SavedSquad.Stats.BattlesFought++;
                    Squad.Kill();
                }
                else
                {
                    Squad.SetOffsets();
                }
                Destroy(gameObject);
            }
            
        }
    }
}