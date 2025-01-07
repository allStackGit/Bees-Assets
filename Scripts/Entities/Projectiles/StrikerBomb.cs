using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class StrikerBomb : MonoBehaviour
    {
        public int Power;
        public Striker Striker;
        public Ship ContactedShip;
        public GameObject Explosion;
        // Use this for initialization
        public void Setup(int power, Striker striker, Ship contactedShip)
        {
            Power = power;
            Striker = striker;
            ContactedShip = contactedShip;
            Invoke(nameof(Explode), 1.5f);

        }

        private void Explode()
        {
            Ship.LogAttackingDamage(Power, Striker.Bomb.BaseProjectile, ContactedShip);
            GameObject explosion = Instantiate(Explosion, transform.position, Quaternion.identity);
            explosion.transform.parent = ContactedShip.transform;
            Destroy(gameObject);
        }
    }
}