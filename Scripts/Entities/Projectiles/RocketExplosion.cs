
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{

    // Explosions don't move but they damage all targets that they touch
    // They operate like normal projectiles upon contact except that contact doesn't kill them and since they linger they can only damage a target once
    public class RocketExplosion : Projectile
    {
        private bool _isHarmless; // After a few frames, the explosion is no longer damaging and becomes harmless to whoever hits it
        private List<Ship> _shipsHit = new List<Ship>();
        private List<Obstacle> _obstaclesHit = new List<Obstacle>();

        public override void ContactTarget(Ship target)
        {
            //Debugger.Log($"Explosion hit {target.Name}");
            _shipsHit.Add(target);
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
           if (obstacle != null)
            {
                if (!HasHitObstacle(obstacle)){
                    DamageObstacle(obstacle);
                }
            }
        }

        public bool HasHitObstacle(Obstacle obstacle)
        {
            return _obstaclesHit.Contains(obstacle);
        }

        public bool HasHitShip(Ship ship)
        {
            return _shipsHit.Contains(ship);
        }

        public new virtual void Kill()
        {
            //Debugger.Log("Killed off the rocket explosion");
            Level.GetState().RemoveExplosion(this);
            Destroy(gameObject);
        }
        public void SetHarmless()
        {
            _isHarmless = true;
            //Debug.Log($"{Name} is now harmless");
        }

        protected override void FixedUpdate()
        {
            if (!Level.IsPaused)
            {
                if (CollidingQueue.Count > 0)
                {
                    //Debugger.Log("Pulled collision off of rocket explosion queue");
                    for (int i = 0; i < CollidingQueue.Count; i++)
                    {
                        ShipCollision(CollidingQueue.Dequeue());
                    }
                }
            }
        }

        protected override void ShipCollision(Ship ship)
        {
            //Debugger.Log($"Rocket explosion collided with {ship.Name}");
            if (ship != null)
            {
                // if hit enemy projectile or fire ship explosion
                if ((!IsFriendly(ship) || (Shooter.ShipType == "Fire Ship" && !Equals(Shooter))))
                {
                    if (!_isHarmless && !HasHitShip(ship)) // if it's an explosion it should do damage but not if it's already contacted the ship
                    {
                        ContactTarget(ship);
                        Ship.LogDamage(Power, Shooter, ship);
                    }

                }
            }
        }

    }




}
