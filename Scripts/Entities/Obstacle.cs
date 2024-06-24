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
        public Collider2D Collider, ProximityCollider;

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
            Level.Pathfinder.AddToUpdateList(Id);
            Level.GetState().RemoveObstacle(this);
            Destroy(gameObject);
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }
    }
}