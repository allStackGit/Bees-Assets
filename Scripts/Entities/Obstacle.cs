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
        public string Name;
        public bool IsMobile, IsMapBorder;
        public LevelStage Level;
        public Collider2D Collider;
        // Use this for initialization
        private void Awake()
        {
            InitialHealth = Health;
            //Debug.Log($"Obstacle has awoken: {Name}: {Health}");
        }

        public void Setup(LevelStage level, int id)
        {
            Level = level;
            Id = id;
            Name = $"{Name} #{Id}";
            gameObject.name = Name;
        }
        public void Kill()
        {
            Debug.Log($"Killing {Name}");
            Level.Pathfinder.AddToUpdateList(Id);
            Destroy(gameObject);
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }
    }
}