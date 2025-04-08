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
        public bool HasExplosion, ShipIsDead, HasBody, HasCollider;
        /// <summary>
        /// This projectile has an animation sequence that must execute for non-visual reasons (e.g. rocket and fire ship explosions)
        /// </summary>
        public bool HasNecessaryAnimation;
        public Animator Animator;

        /// <summary>
        /// If a projectile is dead that means it has been created in the object pool but either has never been spawned into the game or has died and gone back to the pool
        /// </summary>
        public bool IsDead;
        
        public override void Create(Stage stage)
        {
            base.Create(stage);
            if (!Stage.IsTraining && HasExplosion)
            {
                //Debug.Log($"{Stage}, {Stage?.Prefabs}, {Type}, {Stage?.Prefabs?.ConvertProjectileTypeToExplosionAnimation[Type]}");

                Explosion = Instantiate(Stage.Prefabs.ConvertProjectileTypeToExplosionAnimation[Type], Vector2.zero, Quaternion.identity);
                Explosion.gameObject.SetActive(false);
            }
            else
            {
                HasExplosion = false;
            }
            Name = name;
            if (!Stage.IsRendering && !HasNecessaryAnimation)
            {
                Destroy(Animator);
            }

            Deactivate();
        }
        public virtual void Setup(Level level, Weapon weapon, Ship shooter, Ship target, Vector2 startingPosition, float angle, int range, int power)
        {
            Level = level;
            Id = Level.State.GetId();
            Weapon = weapon;
            Shooter = shooter;
            Target = target;
            Range = range;
            Power = power;
            Angle = angle;
            Name = $"{Shooter.Name}: {Type} - #{Id}";
            gameObject.name = Name;
            StartingPosition = startingPosition;
            Transform.parent = Level.Map.Transform;
            Transform.localPosition = StartingPosition;
            IsDead = false;
            Level.State.AddProjectile(this);
            Rotation = -(Angle * Mathf.Rad2Deg);


            FleetShip = shooter.FleetShip;
            SavedSquad = shooter.Squad.SavedSquad;
            ClearData();
            Activate();
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
                //Debug.Log($"{Name} has been killed and will be returned");
                Level.State.RemoveProjectile(this);
                Stage.Pool.ReturnProjectileToPool(this);
                Deactivate();
            }
        }

        public virtual void ContactTarget(Ship target)
        {
            //Debug.Log($"{Name} contacted {target.Name}");
            KillSequence();
        }
        private Vector3 _reverse = new Vector3(0, 0, 180);
        public virtual void KillSequence()
        {
            if (HasExplosion)
            {
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.transform.eulerAngles = Transform.eulerAngles - _reverse;
                Explosion.SetActive(true);
            }
            Kill();
        }

        public virtual void ContactObstacle(Obstacle obstacle)
        {
            if (!obstacle.IsDead)
            {
                if (obstacle.ObstacleType != ConfigData.ObstacleTypes.MapBorder)
                {
                    if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
                    {
                        DamageObstacle((CollisionAsteroid)obstacle);
                    }
                    KillSequence();
                }
                else
                {
                    Kill();
                }

            }
        }

        public void DamageObstacle(CollisionAsteroid asteroid)
        {
            asteroid.Health -= Power;
            if (asteroid.Health <= 0)
            {
                asteroid.Kill(false);
            }
            else if (asteroid.CheckForCrackedSprite())
            {
                asteroid.SwitchToCrackedSprite();
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
                //Debug.Log($"Projectile ({Name}) killed because it went past its range ({Range}), and it's shooter ({FleetShip.Name}) is dead");
                Kill();
            }
            //else if (Level.State.GameOver)
            //{
            //    Debug.Log($"Level ended, killing projectile {Name}");
            //    Kill();
            //}

        }

        ShipDamageStatus _status;
        public virtual void RemoveDamageSentEntry()
        {
            if (Target != null)
            {
                _status = Level.State.GetShipDamageStatus(Shooter.Side, Target);

                if (_status.TotalDamageSentToShip >= Power)
                {
                    _status.TotalDamageSentToShip -= Power;
                }
                else
                {
                    _status.TotalDamageSentToShip = 0;
                }
            }


        }

        /// <summary>
        /// Sets the initial movement of the projectile and only needs to be set once unless it's a tracking projectile, in which case this won't work
        /// </summary>
        protected void SetMovement()
        {
            Transform.eulerAngles = Vector3.forward * Rotation;

            Body.linearVelocity = new Vector2((float)(-Speed * Mathf.Sin(Angle)), (float)(-Speed * Mathf.Cos(Angle)));
            //Debug.Log($"Setting {Name} to an initial velocity of {Body.velocity}");
        }

        private GameObject _collidingThing;
        protected virtual void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            _collidingThing = collider.gameObject;
            //Debug.Log($"Projectile {Name} collided with {collidingThing.name}");
            if (_collidingThing.CompareTag("Ship"))
            {
                CollidingQueue.Enqueue(_collidingThing.GetComponent<Ship>());

            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                CollidingObstacleQueue.Enqueue(_collidingThing.GetComponent<Obstacle>());
            }
        }

        private int _originalPower;
        protected virtual void ShipCollision(Ship ship)
        {
            //Debug.Log("Basic ship collision");
            //Debug.Log($"{Name} hit {ship.Name}. Contact? {(!IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && this != Shooter)) && !ShipsToIgnore.Contains(ship)}," +
            //    $"IsFriendly? {IsFriendly(ship)}");
            // if hit enemy projectile or Fire Barge explosion. the ships to ignore is for leafcutter split shots
            if ((!IsFriendly(ship) || (Shooter.ShipType == ConfigData.ShipTypes.FireBarge && this != Shooter)) && !ShipsToIgnore.Contains(ship))
            {
                _originalPower = Power;
                ContactTarget(ship);
                Ship.LogAttackingDamage(_originalPower, Shooter, FleetShip, SavedSquad, ship);
            }

        }
        public override void Activate()
        {
            //gameObject.SetActive(true);
            //Debug.Log($"Activating {Name}");

            if (HasBody)
            {
                Body.simulated = true;
            }
            //if (HasCollider)
            //{
            //    Collider.enabled = true;
            //}
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = true;
                Animator.enabled = true;
            }
            else if (HasNecessaryAnimation)
            {
                Animator.enabled = true;
            }
            enabled = true;

        }
        public override void Deactivate()
        {
            //gameObject.SetActive(false);
            //Debug.Log($"Deactivating {Name}");

            if (HasBody)
            {
                Body.simulated = false;
            }
            //if (HasCollider)
            //{
            //    Collider.enabled = false;
            //}
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = false;
                Animator.enabled = false;
            }
            else if (HasNecessaryAnimation)
            {
                Animator.enabled = false;
            }
            enabled = false;
        }
    }
    
    
}

