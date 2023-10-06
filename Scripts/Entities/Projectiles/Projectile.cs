using System;
using System.Collections.Generic;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
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
        public GameObject ExplosionPrefab;
        public GameObject Explosion;
        public List<Ship> ShipsToIgnore = new List<Ship>();


        public bool HasExplosion => ExplosionPrefab != null;
        
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
            StartingPosition = startingPosition;
            transform.localPosition = StartingPosition;
            Level = level;
            Body = GetComponent<Rigidbody2D>();
        }

        public virtual void Kill()
        {
            //Debugger.Log($"killed projectile {name}");
            Level.GetState().RemoveProjectile(this);

            Destroy(gameObject);
        }

        public virtual void ContactTarget(Ship target)
        {
            //Debugger.Log($"Projectile hit {target.name}");
            if (HasExplosion)
            {
                Explosion =  Instantiate(ExplosionPrefab, Vector2.zero, Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
                Explosion.transform.localPosition = GetPosition();
            }
            Kill();
        }
        
        protected override void Update()
        {
            if (!Level.IsPaused)
            {
                base.Update();
                if (Range > 0)
                {
                    Move();
                }
               
            }
            
        }

        private bool OutOfBounds()
        {
            Vector2 position = GetPosition();
            if (Level.IsTraining)
            {
                return DistanceToPoint(StartingPosition) >= Range; // [alert] [rl-training] this should only be on to account for higher timescales with RL training

            }
            return DistanceToPoint(StartingPosition) >= Range || position.x >= Level.MaxX || position.x <= Level.MinX || position.y >= Level.MaxY || position.y <= Level.MinY;
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

        private void Move()
        {

            if (OutOfBounds())
            {
                //Debugger.Log($"Projectile when out of bounds! Range:  {Range}");
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
    }
    
    
}