using System.Collections.Generic;
using Assets.Scripts.Data;
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
        public bool HasNecessaryAnimation;
        public Animator Animator;
        public bool IsDead;
        private ShipDamageStatus _damageReservation;

        public override void Create(Stage stage)
        {
            base.Create(stage);
            if (!Stage.IsTraining && HasExplosion)
            {
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
            ClearData();
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
            _damageReservation = target != null ? Level.State.GetShipDamageStatus(shooter.Side, target) : null;
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
            _damageReservation = null;
        }

        public virtual void Kill()
        {
            if (IsDead)
            {
                return;
            }

            RemoveDamageSentEntry();
            Shooter?.ProjectilesInFlight.Remove(this);
            IsDead = true;
            Level.State.RemoveProjectile(this);
            Stage.Pool.ReturnProjectileToPool(this);
            Deactivate();
        }

        public virtual void ContactTarget(Ship target)
        {
            KillSequence();
        }

        private readonly Vector3 _reverse = new Vector3(0, 0, 180);
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
            if (obstacle.IsDead)
            {
                return;
            }

            if (obstacle.ObstacleType == ConfigData.ObstacleTypes.MapBorder)
            {
                Kill();
                return;
            }

            if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
            {
                DamageObstacle((CollisionAsteroid)obstacle);
            }
            KillSequence();
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
            if (IsDead)
            {
                return;
            }

            if (CollidingQueue.Count > 0)
            {
                ShipCollision(CollidingQueue.Dequeue());
                if (IsDead)
                {
                    return;
                }
            }
            if (CollidingObstacleQueue.Count > 0)
            {
                ContactObstacle(CollidingObstacleQueue.Dequeue());
                if (IsDead)
                {
                    return;
                }
            }
            if (ShipIsDead && DistanceToPoint(StartingPosition) > Range)
            {
                Kill();
            }
        }

        public void TransferDamageReservationTo(Projectile recipient)
        {
            if (recipient == null || ReferenceEquals(recipient, this))
            {
                return;
            }

            recipient._damageReservation = _damageReservation;
            _damageReservation = null;
        }

        public virtual void RemoveDamageSentEntry()
        {
            if (_damageReservation == null)
            {
                return;
            }

            if (_damageReservation.TotalDamageSentToShip >= Power)
            {
                _damageReservation.TotalDamageSentToShip -= Power;
            }
            else
            {
                _damageReservation.TotalDamageSentToShip = 0;
            }
            _damageReservation = null;
        }

        protected void SetMovement()
        {
            Transform.eulerAngles = Vector3.forward * Rotation;
            Body.linearVelocity = new Vector2((float)(-Speed * Mathf.Sin(Angle)), (float)(-Speed * Mathf.Cos(Angle)));
        }

        private GameObject _collidingThing;
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
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
            if (ProjectileDamagePolicy.CanBasicProjectileDamage(
                Shooter.Side,
                Shooter.ShipType,
                ship.Side,
                ShipsToIgnore.Contains(ship)))
            {
                _originalPower = Power;
                ContactTarget(ship);
                Ship.LogAttackingDamage(_originalPower, Shooter, FleetShip, SavedSquad, ship);
            }
        }

        public override void Activate()
        {
            if (HasBody)
            {
                Body.simulated = true;
            }
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = true;
                Animator.enabled = true;
                Animator.Rebind();
                Animator.Update(0f);
            }
            else if (HasNecessaryAnimation)
            {
                Animator.enabled = true;
            }
            enabled = true;
        }

        public override void Deactivate()
        {
            if (HasBody)
            {
                Body.simulated = false;
            }
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