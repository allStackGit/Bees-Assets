
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
            Body.velocity *= 1.5f;
        }

        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"Rocket hit {target.Name} and exploded");
            KillSequence();
        }

        public override void KillSequence()
        {
            CancelInvoke(nameof(IncreaseSpeed));
            AddExplosion();
            Kill();
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
            if (obstacle != null)
            {
                if (!obstacle.IsMapBorder)
                {
                    //Debug.Log($"{Name} hit {obstacle.Name}");
                    //DamageObstacle(obstacle);
                    KillSequence();
                }
                Kill();

            }
        }

        private void AddExplosion()
        {
            Explosion = Instantiate(Explosion, new Vector3(0, 0, 0), Quaternion.identity);
            Explosion.transform.parent = Level.Map.transform;
            RocketExplosion explosion = (RocketExplosion) Explosion.GetComponent(typeof(RocketExplosion));
            explosion.Setup(this.Level, this.Side, Level.State.GetId(), this.Weapon, this.Shooter, this.Target, this.GetPosition(), 0, 0, this.Power);
            Shooter.ProjectilesInFlight.Add(explosion);
        }

        protected override void ShipCollision(Ship ship)
        {
            //Debug.Log("Basic rocket collision");
            if (ship != null)
            {
                // if hit enemy projectile or Fire Barge explosion. the ships to ignore is for leafcutter split shots
                if ((!IsFriendly(ship) || (Shooter.ShipType == "Fire Barge" && !Equals(Shooter))) && !ShipsToIgnore.Contains(ship))
                {
                    ContactTarget(ship); // don't do damage because the explosion is what does damage
                }
            }
        }
    }

    


}
