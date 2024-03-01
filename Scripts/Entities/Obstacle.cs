using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Obstacle : MonoBehaviour
    {
        public int Health;
        public int InitialHealth;
        public string Name;
        // Use this for initialization
        private void Awake()
        {
            InitialHealth = Health;
            Debug.Log($"Obstacle has awoken: {Name}: {Health}");
        }
        public void Kill()
        {
            Debug.Log($"Killing {Name}");
            Destroy(gameObject);
        }
    }
}