using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    // A collider for clearinng fog of war for ships that don't have range colliders
    public class Vision : MonoBehaviour
    {

        public CircleCollider2D Collider;

        public virtual void Setup(Ship ship)
        {
            //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");
            Collider.radius = ship.Sight;
            //Debug.Log($"{ship.Name} : {gameObject.name} Collider.radius: {Collider.radius}, Sight: {ship.Sight}");

        }
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Fog of War"))
            {
                Destroy(collidingThing);
            }

        }
    }
}