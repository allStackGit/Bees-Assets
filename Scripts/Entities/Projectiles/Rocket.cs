
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
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
        public RocketExplosion RocketExplosion;
        private ScaledTimer _speedIncreaseTimer = new ScaledTimer();

        public override void Setup(Level level, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            base.Setup(level, weapon, shooter, target, startingPosition, angle, range, power);
            _speedIncreaseTimer.Reuse(.1f, IncreaseSpeed, true);
            Level.AddTimer(_speedIncreaseTimer);
            if (Stage.IsRendering)
            {
                Animator.Play("Missile Release", 0, 0f);
            }


            //InvokeRepeating(nameof(IncreaseSpeed), .1f, .1f);
        }
        private void IncreaseSpeed()
        {
            Body.linearVelocity = Vector2.ClampMagnitude(Body.linearVelocity * 1.5f, MaxSpeed);
        }
        public override void Kill()
        {
            Level.CancelTimer(_speedIncreaseTimer);
            base.Kill();
        }
        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"Rocket hit {target.Name} and exploded");
            KillSequence();
        }

        public override void KillSequence()
        {
            //CancelInvoke(nameof(IncreaseSpeed));
            //Level.CancelTimer(_speedIncreaseTimer);
            AddExplosion();
            Kill();
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
            if (obstacle != null)
            {
                if (obstacle.ObstacleType != ConfigData.ObstacleTypes.MapBorder)
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
            //Debug.Log($"{Name} is dying and dropping an explosion");
            RocketExplosion = (RocketExplosion)Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.RocketExplosion);
            RocketExplosion.transform.parent = Level.Map.Transform;
            // The rocket already owns the target reservation created when it was fired.
            // The explosion is its damage-delivery continuation, so transfer that exact
            // reservation instead of creating a second owner for the same damage.
            RocketExplosion.Setup(Level, Weapon, Shooter, null, GetPosition(), 0, 0, Power);
            RocketExplosion.InheritCommandAttributionFrom(this);
            TransferDamageReservationTo(RocketExplosion);
            Shooter.ProjectilesInFlight.Add(RocketExplosion);
        }

        protected override void ShipCollision(Ship ship)
        {
            //Debug.Log("Basic rocket collision");
            if (ship != null)
            {
                // if hit enemy projectile or Fire Barge explosion. the ships to ignore is for leafcutter split shots
                if ((!IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && this != Shooter)) && !ShipsToIgnore.Contains(ship))
                {
                    ContactTarget(ship); // don't do damage because the explosion is what does damage
                }
            }
        }
    }

    


}
