
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
        private HashSet<Ship> _shipsHit = new HashSet<Ship>();
        private HashSet<Obstacle> _obstaclesHit = new HashSet<Obstacle>();
        public CircleCollider2D CircleCollider;

        public override void ClearData()
        {
            base.ClearData();
            _shipsHit.Clear();
            _obstaclesHit.Clear();
            _isHarmless = false;
        }
        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"Explosion hit {target.Name}");
            _shipsHit.Add(target);
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
           if (obstacle != null)
            {
                if (obstacle.ObstacleType != ConfigData.ObstacleTypes.MapBorder && !HasHitObstacle(obstacle))
                {
                    //Debug.Log($"{Name} hit {obstacle.Name}");
                    if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
                    {
                        DamageObstacle((CollisionAsteroid)obstacle);
                    }
                    //DamageObstacle(obstacle);
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

        public override void Kill()
        {
            //Debug.Log("Killed off the rocket explosion");
            if (Type == ConfigData.ProjectileTypes.FireBargeExplosion)
            {
                Level.State.FireBargeExplosions.Remove(this);
            }
            base.Kill();
        }
        public override void Activate()
        {
            base.Activate();
            Animator.enabled = true;
        }
        public override void Deactivate()
        {
            base.Deactivate();
            Animator.enabled = false;
        }

        public void SetHarmless()
        {
            _isHarmless = true;
            //Debug.Log($"{Name} is now harmless");
        }
        public void SetColliderSize(int size)
        {
            //Debug.Log($"Setting collider size to {size} for {Name}");
            CircleCollider.radius = size;
        }
        private int _index;
        protected override void FixedUpdate()
        {
            if (!Level.State.IsPaused)
            {
                if (CollidingQueue.Count > 0)
                {
                    //Debug.Log($"Pulled collision for {Name} off of rocket explosion queue");
                    for (_index = 0; _index < CollidingQueue.Count; _index++)
                    {
                        ShipCollision(CollidingQueue.Dequeue());
                    }
                }
                if (CollidingObstacleQueue.Count > 0)
                {
                    ContactObstacle(CollidingObstacleQueue.Dequeue());
                }
            }
        }

        protected override void ShipCollision(Ship ship)
        {
            //Debug.Log($"Rocket explosion collided with {ship.Name}");
            if (ship != null)
            {
                // if hit enemy projectile or Fire Barge explosion
                if ((!IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && this != Shooter)))
                {
                    if (!_isHarmless && !HasHitShip(ship)) // if it's an explosion it should do damage but not if it's already contacted the ship
                    {
                        ContactTarget(ship);
                        Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ship);
                    }

                }
            }
        }

    }




}
