using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;

using UnityEngine;

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
        public GameObject Explosion;
        public HashSet<Ship> ShipsToIgnore = new HashSet<Ship>();
        public Queue<Ship> CollidingQueue = new Queue<Ship>();
        public Queue<Obstacle> CollidingObstacleQueue = new Queue<Obstacle>();
        public string Name;
        public ConfigData.ProjectileTypes Type;
        public bool HasExplosion, ShipIsDead, HasBody;
        /// <summary>
        /// If a projectile is dead that means it has been created in the object pool but either has never been spawned into the game or has died and gone back to the pool
        /// </summary>
        public bool IsDead;
        
        public void Create(Stage stage)
        {
            Stage = stage;
            if (!Stage.IsTraining && HasExplosion)
            {
                //Debug.Log($"{Stage}, {Stage?.Prefabs}, {Type}, {Stage?.Prefabs?.ConvertProjectileTypeToExplosionAnimation[Type]}");
                
                if (Type == ConfigData.ProjectileTypes.Rocket)
                {
                    Explosion = Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.RocketExplosion).gameObject;
                }
                else
                {
                    Explosion = Instantiate(Stage.Prefabs.ConvertProjectileTypeToExplosionAnimation[Type], Vector2.zero, Quaternion.identity);
                }
                Explosion.gameObject.SetActive(false);

            }
            else
            {
                HasExplosion = false;
            }

            gameObject.SetActive(false);
        }
        public virtual void Setup(Level level, long id, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            Level = level;
            Id = id;
            Weapon = weapon;
            Shooter = shooter;
            Target = target;
            Range = range;
            Power = power;
            Angle = angle;
            Name = $"{Shooter.Name}: {Type} - #{Id}";
            gameObject.name = Name;
            StartingPosition = startingPosition;
            transform.parent = Level.Map.transform;
            transform.localPosition = StartingPosition;
            IsDead = false;



            FleetShip = shooter.FleetShip;
            SavedSquad = shooter.Squad.SavedSquad;
            ClearData();
            gameObject.SetActive(true);
            if (HasBody)
            {
                SetMovement();
            }
        }

        public virtual void ClearData()
        {
            ShipsToIgnore.Clear();
            CollidingQueue.Clear();
            CollidingObstacleQueue.Clear();
            ShipIsDead = false;

        }

        public virtual void Kill()
        {
            //Debug.Log($"killed projectile {Name}");
            if (!IsDead)
            {
                RemoveDamageSentEntry();
                if (!ShipIsDead)
                {
                    Shooter.ProjectilesInFlight.Remove(this);
                }
                IsDead = true;
                Debug.Log($"{Name} has been killed and will be returned");
                Stage.Pool.ReturnProjectileToPool(this);
            }

            //Destroy(gameObject);
        }

        public virtual void ContactTarget(Ship target)
        {
            //Debug.Log($"Projectile hit {target.name}");
            KillSequence();
        }
        public virtual void KillSequence()
        {
            if (HasExplosion)
            {
                Explosion.transform.parent = Level.Map.transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.transform.eulerAngles = transform.eulerAngles - new Vector3(0, 0, 180);
                Explosion.SetActive(true);
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

        public void RemoveDamageSentEntry()
        {
            if (Target != null)
            {
                ShipDamageStatus status = Level.State.GetShipDamageStatus(Shooter.Side, Target);
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
            //Debug.Log($"Projectile {Name} collided with {collidingThing.name}");
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
                // if hit enemy projectile or Fire Barge explosion. the ships to ignore is for leafcutter split shots
                if ((!IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && !Equals(Shooter))) && !ShipsToIgnore.Contains(ship))
                {
                    int originalPower = Power;
                    ContactTarget(ship);
                    Ship.LogAttackingDamage(originalPower, Shooter, FleetShip, SavedSquad, ship);
                }
            }

        }
    }
    
    
}

