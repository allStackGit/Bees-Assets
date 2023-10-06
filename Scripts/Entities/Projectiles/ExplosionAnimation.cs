using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class ExplosionAnimation : MonoBehaviour
    {
        public void Kill()
        {
            Destroy(gameObject);
        }
    }
}