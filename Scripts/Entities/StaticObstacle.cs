using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class StaticObstacle : Obstacle
    {

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private int _frameCollisions;
        public void Collision(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                ShipCollision(_collidingShip);
            }
        }

        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            Collision(collider);
        }
        protected virtual void OnTriggerStay2D(Collider2D collider)
        {
            if (ObstacleType == ConfigData.ObstacleTypes.StaticObstacle)
            {
                _frameCollisions++;
                if (_frameCollisions == 50)
                {
                    Collision(collider);
                    _frameCollisions = 0;
                }
            }

        }
    }
}