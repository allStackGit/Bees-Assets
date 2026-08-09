using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    // Explosions don't move but they damage all targets that they touch. They operate like
    // normal projectiles except that contact does not kill them and each target is damaged once.
    public class RocketExplosion : Projectile
    {
        public bool IsHarmless;
        public List<Ship> _shipsHit = new List<Ship>();
        private readonly HashSet<Obstacle> _obstaclesHit = new HashSet<Obstacle>();
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
            _shipsHit.Add(target);
        }

        public override void ContactObstacle(Obstacle obstacle)
        {
            if (obstacle.IsDead || obstacle.ObstacleType == ConfigData.ObstacleTypes.MapBorder || HasHitObstacle(obstacle))
            {
                return;
            }

            _obstaclesHit.Add(obstacle);
            if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
            {
                DamageObstacle((CollisionAsteroid)obstacle);
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
            if (Type == ConfigData.ProjectileTypes.FireBargeExplosion)
            {
                Level.State.FireBargeExplosions.Remove(this);
            }
            base.Kill();
        }

        public void SetHarmless()
        {
            IsHarmless = true;
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
            CircleCollider.radius = size;
        }

        protected override void FixedUpdate()
        {
            // Count changes on Dequeue, so a for-loop bounded by the live Count skips roughly
            // half of simultaneous contacts. Drain the queues instead.
            while (CollidingQueue.Count > 0)
            {
                ShipCollision(CollidingQueue.Dequeue());
            }
            while (CollidingObstacleQueue.Count > 0)
            {
                ContactObstacle(CollidingObstacleQueue.Dequeue());
            }
        }

        protected override void ShipCollision(Ship ship)
        {
            if (ProjectileDamagePolicy.CanExplosionDamage(
                Shooter.Side,
                Shooter.ShipType,
                ship.Side,
                Type,
                ship.IsDead,
                IsHarmless,
                HasHitShip(ship)))
            {
                ContactTarget(ship);
                Ship.LogAttackingDamage(Power, Shooter, FleetShip, SavedSquad, ship);
            }
        }
    }
}
