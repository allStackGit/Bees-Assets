using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;

using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class Projectile : Entity
    {
        public int Power, Range;
        public double Speed;
        public Ship Shooter, Target;
        public FleetShip FleetShip;
        public SavedSquad SavedSquad;
        public Weapon Weapon;
        public Vector2 StartingPosition;
        public float Angle;
        public GameObject ExplosionAnimationPrefab;
        public GameObject Explosion;
        public HashSet<Ship> ShipsToIgnore = new HashSet<Ship>();
        public Queue<Ship> CollidingQueue = new Queue<Ship>();
        public Queue<Obstacle> CollidingObstacleQueue = new Queue<Obstacle>();
        public string Name;
        public bool HasExplosion, HasSetMovement, ShipIsDead;
        
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
            gameObject.name = Name;
            StartingPosition = startingPosition;
            transform.localPosition = StartingPosition;
            Level = level;
            Body = GetComponent<Rigidbody2D>();
            Collider = GetComponent<Collider2D>();
            HasExplosion = ExplosionAnimationPrefab != null;
            if (Body != null)
            {
                SetMovement();
            }

            FleetShip = shooter.FleetShip;
            SavedSquad = shooter.Squad.SavedSquad;
        }

        public virtual void Kill()
        {
            //Debug.Log($"killed projectile {Name}");
            RemoveDamageSentEntry();
            if (!ShipIsDead)
            {
                Shooter.ProjectilesInFlight.Remove(this);
            }
            Destroy(gameObject);
        }

        public virtual void ContactTarget(Ship target)
        {
            //Debug.Log($"Projectile hit {target.name}");
            KillSequence();
        }
        public virtual void KillSequence()
        {
            if (!Level.IsTraining && HasExplosion)
            {
                Explosion = Instantiate(ExplosionAnimationPrefab, Vector2.zero, Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.transform.eulerAngles = transform.eulerAngles - new Vector3(0, 0, 180);
            }
            Kill();
        }

        public virtual void ContactObstacle(Obstacle obstacle)
        {
            if (obstacle != null)
            {
                if (!obstacle.IsMapBorder)
                {
                    if (obstacle.IsCollisionAsteroid)
                    {
                        DamageObstacle(obstacle);
                    }
                    KillSequence();
                }
                else
                {
                    Kill();
                }

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
            if (!Level.IsPaused)
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
                if (ShipIsDead && DistanceToPoint(StartingPosition) > Range)
                {
                    Debug.Log($"Projectile ({Name}) killed because it went past its range ({Range}), and it's shooter ({FleetShip.Name}) is dead");
                    Kill();
                }
            }
            
        }

        public void RemoveDamageSentEntry()
        {
            if (Target != null)
            {
                ShipDamageStatus status = Shooter.Squad.GetShipDamageStatus(Target);
                if (status.TotalDamageSentToShip >= Power)
                {
                    status.TotalDamageSentToShip -= Power;
                }
                else
                {
                    status.TotalDamageSentToShip = 0;
                }
            }

        }

        /// <summary>
        /// Sets the initial movement of the projectile and only needs to be set once unless it's a tracking projectile, in which case this won't work
        /// </summary>
        protected void SetMovement()
        {
            transform.eulerAngles = Vector3.back * (Angle * Mathf.Rad2Deg);

            Body.velocity = new Vector2((float)(-Speed * Mathf.Sin(Angle)), (float)(-Speed * Mathf.Cos(Angle)));

        }

        protected virtual void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            Debug.Log($"Projectile {Name} collided with {collidingThing.name}");
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
                    int originalPower = Power;
                    ContactTarget(ship);
                    Ship.LogAttackingDamage(originalPower, Shooter, FleetShip, SavedSquad, ship);
                }
            }

        }
    }
    
    
}

