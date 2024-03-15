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
        public bool IsMobile;
        LevelStage Level;
        // Use this for initialization
        private void Awake()
        {
            InitialHealth = Health;
            gameObject.name = Name;
            Debug.Log($"Obstacle has awoken: {Name}: {Health}");
        }

        public void Setup(LevelStage level, int id)
        {
            Level = level;
            Id = id;
        }
        public void Kill()
        {
            Debug.Log($"Killing {Name}");
            Level.Pathfinder.AddToUpdateList(Id);
            Destroy(gameObject);
        }
    }
}