using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class Projectile : Entity
    {
        public int Range, Power;
        public double Speed;
        public Ship Shooter, Target;
        public Weapon Weapon;
        public Vector2 StartingPosition;
        public float Angle;
        public GameObject ExplosionAnimationPrefab;
        public GameObject Explosion;
        public List<Ship> ShipsToIgnore = new List<Ship>();
        public Queue<Ship> CollidingQueue = new Queue<Ship>();
        public Queue<Obstacle> CollidingObstacleQueue = new Queue<Obstacle>();
        public string Name;
        public bool HasExplosion;
        
        public void Setup(LevelStage level, int side, long id, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            base.Id = id;
            this.Weapon = weapon;
            this.Shooter = shooter;
            this.Target = target;
            this.Range = range;
            this.Side = side;
            this.Power = power;
            this.Angle = angle;
            Name = $"{Shooter.Name}: {name} - #{Id}";
            StartingPosition = startingPosition;
            transform.localPosition = StartingPosition;
            Level = level;
            Body = GetComponent<Rigidbody2D>();
            gameObject.name = Name;
            HasExplosion = ExplosionAnimationPrefab != null;
        }

        public virtual void Kill()
        {
            //Debug.Log($"killed projectile {Name}");
            Level.GetState().RemoveProjectile(this);

            Destroy(gameObject);
        }

        public virtual void ContactTarget(Ship target)
        {
            //Debug.Log($"Projectile hit {target.name}");
            KillSequence();
        }
        public virtual void KillSequence()
        {
            if (!Level.IsTrainingNueralNetwork && !Level.IsTrainingHiveMind && HasExplosion)
            {
                Explosion = Instantiate(ExplosionAnimationPrefab, Vector2.zero, Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
                Explosion.transform.localPosition = GetPosition();
            }
            Kill();
        }

        public virtual void ContactObstacle(Obstacle obstacle)
        {
            if (obstacle != null)
            {
                DamageObstacle(obstacle);
                KillSequence();
            }
        }

        public void DamageObstacle(Obstacle obstacle)
        {
            obstacle.Health -= Power;
            if (obstacle.Health <= 0)
            {
                obstacle.Kill();
            }
        }
        
        protected virtual void FixedUpdate()
        {
            if (!Level.IsPaused && Range > 0)
            {
                if (CollidingQueue.Count > 0)
                {
                    //Debug.Log("Pulled collision off of queue");
                    ShipCollision(CollidingQueue.Dequeue());
                }
                if (CollidingObstacleQueue.Count > 0)
                {
                    ContactObstacle(CollidingObstacleQueue.Dequeue());
                }
                Move();
            }
            
        }

        private bool OutOfBounds()
        {
            Vector2 position = GetPosition();
            if (Level.IsTrainingNueralNetwork)
            {
                return DistanceToPoint(StartingPosition) >= Range; // [alert] [rl-training] this should only be on to account for higher timescales with RL training

            }
            bool outOfBounds = DistanceToPoint(StartingPosition) >= Range || position.x > Level.MaxX || position.x < Level.MinX || position.y > Level.MaxY || position.y < Level.MinY;
            //if (outOfBounds)
            //{
            //    Debug.Log($"Projectile ({name}) #{Id} at position ({position}) is out of bounds with a distance to starting point ({StartingPosition}) of ({DistanceToPoint(StartingPosition)})");
            //}
            return outOfBounds;
        }

        protected void RemoveDamageSentEntry()
        {
            if (Target != null)
            {
                ShipDamageStatus status = Shooter.Squad.GetShipDamageStatus(Target);
                if (status.totalDamageSentToShip > Power)
                {
                    status.totalDamageSentToShip -= Power;
                }
            }

        }

        protected void Move()
        {
            //Debug.Log($"Moving position for #{Id}: {GetPosition()}");
            if (OutOfBounds())
            {
                //Debug.Log($"Projectile ({name}) #{Id} went out of bounds! Range:  {Range}");
                RemoveDamageSentEntry();
                Kill();
            }
            else
            {
                transform.eulerAngles = Vector3.back * (Angle * Mathf.Rad2Deg);

                float x = (float)(-1 * Speed * Math.Sin(Angle));
                float y = (float)(-1 * Speed * Math.Cos(Angle));

                Body.velocity = new Vector3(x, y);
            }
        }

        protected virtual void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                CollidingQueue.Enqueue(ship);

            }
            else if (collidingThing.CompareTag("Obstacle"))
            {
                Obstacle obstacle = collidingThing.GetComponent<Obstacle>();
                CollidingObstacleQueue.Enqueue(obstacle);
            }
        }

        protected virtual void ShipCollision(Ship ship)
        {
            //Debug.Log("Basic ship collision");
            if (ship != null)
            {
                // if hit enemy projectile or fire ship explosion. the ships to ignore is for leafcutter split shots
                if ((!IsFriendly(ship) || (Shooter.ShipType == "Fire Ship" && !Equals(Shooter))) && !ShipsToIgnore.Contains(ship))
                {
                    ContactTarget(ship);
                    Ship.LogDamage(Power, Shooter, ship);
                }
            }

        }
    }
    
    
}