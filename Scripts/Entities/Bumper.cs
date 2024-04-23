using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Bumper : MonoBehaviour
    {
        public Ship Ship;
        public void Setup(Ship ship)
        {
            Ship = ship;
        }

        public void OnCollisionEnter2D(Collision2D collision)
        {
            Debug.Log($"{collision.otherCollider}/Bumper belonging to {Ship.Name} collided with {collision.collider.gameObject.name}");
        }
    }
}