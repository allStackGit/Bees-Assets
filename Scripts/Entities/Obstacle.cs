using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Obstacle : MonoBehaviour
    {
        public int Health, OriginalHealth, Id;
        /// <summary>
        /// The index of the obstacle in the list of obstacle points for the pathfinding map
        /// </summary>
        public int MapPointsIndex;
        public string Name;
        public bool IsDead;
        public Level Level;
        public Stage Stage;
        public ConfigData.ObstacleTypes ObstacleType;
        public Collider2D Collider, ProximityCollider, ClearanceMappingCollider;

        private int _frameCollisions = 0;

        public virtual void Create(Stage stage)
        {
            Stage = stage;
            OriginalHealth = Health;
        }
        public virtual void Setup(Level level)
        {
            ClearData();
            Level = level;
            Id = Level.State.GetId();
            Name = $"{ObstacleType} #{Id}";
            gameObject.name = Name;
            Health = OriginalHealth;
            gameObject.SetActive(true);

        }
        public virtual void ClearData()
        {
            Debug.Log($"Clearing data for {Name}");
            MapPointsIndex = 0;
            IsDead = false;
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }

        public virtual void ShipCollision(Ship ship)
        {
            //Debug.Log($"{Name} was hit by {ship.Name}");
            if (ship.ShipType == ConfigData.ShipTypes.Barge)
            {
                Barge barge = ((Barge)ship);
                if (barge.IsCharging)
                {
                    ship.LogDamage(ship.Health); // kills the ship but logs the damage and tsv change first
                    return;
                }
            }

            ship.LogDamage((int)(ship.MaxHealth * .2f)); // 20% of ship health
        }

        public void Collision(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                ShipCollision(ship);
            }
        }

        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            if (ObstacleType == ConfigData.ObstacleTypes.StaticObstacle)
            {
                Collision(collider);
            }
        }
        protected virtual void OnTriggerStay2D(Collider2D collider)
        {
            if (ObstacleType == ConfigData.ObstacleTypes.StaticObstacle)
            {
                _frameCollisions++;
                if (_frameCollisions == 50)
                {
                    Collision(collider);
                    _frameCollisions = 0;
                }
            }

        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Obstacle x = obj as Obstacle;
            if (x == null)
            {
                return false;
            }

            return Id == x.Id;
        }

        public bool Equals(Obstacle other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(Obstacle a, Obstacle b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(Obstacle a, Obstacle b)
        {
            return !(a == b);
        }
    }
}