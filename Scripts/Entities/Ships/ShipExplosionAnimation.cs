using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipExplosionAnimation : MonoBehaviour
    {

        public void Kill()
        {
            Destroy(gameObject);
        }
    }
}