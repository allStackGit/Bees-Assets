
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{

    // rockets start slow but speed up towards their target
    // they explode on impact, doing damage in a radius
    public class Rocket : Projectile
    {
        //public GameObject RocketExplosion;
        public int MaxSpeed;

        protected void Start()
        {
            InvokeRepeating(nameof(IncreaseSpeed), .1f, .1f);
        }

        private void IncreaseSpeed()
        {
            Speed *= 1.5;
        }

        public override void ContactTarget(Ship target)
        {
            //Debugger.Log($"Rocket hit {target.name} and exploded");
            CancelInvoke(nameof(IncreaseSpeed));
            AddExplosion();
            Kill();
        }

        private void AddExplosion()
        {
            Explosion =  Instantiate(ExplosionPrefab, new Vector3(0, 0, 0), Quaternion.identity);
            Explosion.transform.parent = Level.Map.transform;
            RocketExplosion explosion = (RocketExplosion) Explosion.GetComponent(typeof(RocketExplosion));
            GameState state = Level.GetState();
            explosion.Setup(this.Level, this.Side, state.AddEntity(), this.Weapon, this.Shooter, this.Target, this.GetPosition(), 0, 0, this.Power);
            state.AddExplosion(explosion);
        }

    }

    


}
