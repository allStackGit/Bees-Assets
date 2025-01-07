using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class StrikerBomb : MonoBehaviour
    {
        public int Power;
        public Striker Striker;
        public FleetShip FleetShip;
        public SavedSquad SavedSquad;
        public Ship ContactedShip;
        public GameObject Explosion;
        // Use this for initialization
        public void Setup(int power, Striker striker, Ship contactedShip)
        {
            Power = power;
            Striker = striker;
            FleetShip = Striker.FleetShip;
            SavedSquad = Striker.Squad.SavedSquad;
            ContactedShip = contactedShip;
            Invoke(nameof(Explode), 1.5f);

        }

        private void Explode()
        {
            Ship.LogAttackingDamage(Power, Striker, FleetShip, SavedSquad, ContactedShip);
            GameObject explosion = Instantiate(Explosion, transform.position, Quaternion.identity);
            explosion.transform.parent = ContactedShip.transform;
            Destroy(gameObject);
        }
    }
}