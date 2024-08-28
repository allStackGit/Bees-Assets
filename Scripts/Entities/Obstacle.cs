using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Obstacle : MonoBehaviour
    {
        public int Health, InitialHealth, Id;
        /// <summary>
        /// The index of the obstacle in the list of obstacle points for the pathfinding map
        /// </summary>
        public int MapPointsIndex;
        public string Name;
        public bool IsMobile, IsMapBorder, HasEnteredMap, IsDead, IsMiningAsteroid;
        public LevelStage Level;
        public Collider2D Collider, ProximityCollider, ClearanceMappingCollider;

        private int _frameCollisions = 0;

        public void Setup(LevelStage level, int id)
        {
            Level = level;
            Id = id;
            Name = $"{Name} #{Id}";
            gameObject.name = Name;
            InitialHealth = Health;


        }
        public virtual void Kill()
        {
            //Debug.Log($"Killing {Name}");
            IsDead = true;
            Level.GetState().RemoveObstacle(this);
            Destroy(gameObject);
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }

        public void ShipCollision(Ship ship)
        {
            Debug.Log($"{Name} was hit by {ship.Name}");
            if (ship.ShipType == "Barge")
            {
                Barge barge = ((Barge)ship);
                if (barge.IsCharging)
                {
                    ship.Kill(null);
                    return;
                }
            }

            ship.LogDamage((int)(ship.MaxHealth * .2f)); // 20% of ship health
        }

        public void Collision(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (!IsMapBorder && !IsMobile && collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                ShipCollision(ship);
            }
        }

        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            Collision(collider);
        }
        protected virtual void OnTriggerStay2D(Collider2D collider)
        {
            _frameCollisions++;
            if (_frameCollisions == 50)
            {
                Collision(collider);
                _frameCollisions = 0;
            }
        }
    }
}