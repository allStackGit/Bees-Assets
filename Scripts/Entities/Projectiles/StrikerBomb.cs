using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class StrikerBomb : MonoBehaviour
    {
        public int Power;
        public Ship Striker, ContactedShip;
        public GameObject Explosion;
        // Use this for initialization
        public void Setup(int power, Ship striker, Ship contactedShip)
        {
            Power = power;
            Striker = striker;
            ContactedShip = contactedShip;
            Invoke(nameof(Explode), 1.5f);

        }

        private void Explode()
        {
            Ship.LogDamage(Power, Striker, ContactedShip);
            GameObject explosion = Instantiate(Explosion, transform.position, Quaternion.identity);
            explosion.transform.parent = ContactedShip.transform;
            Destroy(gameObject);
        }
    }
}