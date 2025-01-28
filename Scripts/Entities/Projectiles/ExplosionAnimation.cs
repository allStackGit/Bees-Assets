using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    /// <summary>
    /// For projectile explosion animations
    /// </summary>
    public class ExplosionAnimation : MonoBehaviour
    {
        public void Kill()
        {
            gameObject.SetActive(false);
        }
    }
}