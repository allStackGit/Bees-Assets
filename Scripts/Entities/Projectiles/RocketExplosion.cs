
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
        /// <summary>
        ///  After a few frames, the explosion is no longer damaging and becomes harmless to whoever hits it
        /// </summary>
        public bool IsHarmless;
        public List<Ship> _shipsHit = new List<Ship>();
        private HashSet<Obstacle> _obstaclesHit = new HashSet<Obstacle>();
        public CircleCollider2D CircleCollider;

        public override void ClearData()
        {
            base.ClearData();
            _shipsHit.Clear();
            _obstaclesHit.Clear();
            IsHarmless = false;
        }
        public override void Activate()
        {
            base.Activate();
            CircleCollider.enabled = true;
        }
        public override void Deactivate()
        {
            base.Deactivate();
            CircleCollider.enabled = false;
        }
        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"Explosion {Name} hit {target.Name}");
            _shipsHit.Add(target);
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
           if (!obstacle.IsDead)
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

        public void SetHarmless()
        {
            IsHarmless = true;
            //Debug.Log($"{Name} is now harmless");
        }
        public override void RemoveDamageSentEntry()
        {
            if (Target != null)
            {
                base.RemoveDamageSentEntry();
            }
        }
        public void SetColliderSize(int size)
        {
            //Debug.Log($"Setting collider size to {size} for {Name}");
            CircleCollider.radius = size;
        }
        private int _index;
        protected override void FixedUpdate()
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

        protected override void ShipCollision(Ship ship)
        {
            //Debug.Log($"Rocket explosion {Name} collided with {ship.Name}");
            if (!ship.IsDead)
            {
                bool damagesAllShips = Type == ConfigData.ProjectileTypes.FireTankExplosion;

                // Fire Tank explosions are neutral hazards. Other explosions retain
                // their normal enemy/friendly-fire rules.
                if (damagesAllShips || !IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && this != Shooter))
                {
                    if (!IsHarmless && !HasHitShip(ship)) // if it's an explosion it should do damage but not if it's already contacted the ship
                    {
                        ContactTarget(ship);
                        Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ship);
                    }

                }
            }
        }

    }




}
