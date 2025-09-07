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
        private Barge _barge;
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
            Debug.Log($"Trigger enter {collider}");
            Collision(collider);
        }
        protected virtual void OnTriggerStay2D(Collider2D collider)
        {
            Debug.Log($"Trigger stay {collider}");
            if (ObstacleType == ConfigData.ObstacleTypes.StaticObstacle)
            {
                _frameCollisions++;
                if (_frameCollisions == 25)
                {
                    Collision(collider);
                    _frameCollisions = 0;
                }
            }

        }

        public virtual void ShipCollision(Ship ship)
        {
            Debug.Log($"{Name} was hit by {ship.Name}");
            if (ship.ShipType == ConfigData.ShipTypes.Barge)
            {
                _barge = ((Barge)ship);
                if (_barge.IsCharging)
                {
                    ship.LogDamage(ship.Health); // kills the barge but logs the damage and tsv change first
                    return;
                }
            }
            else if (ship.Speed >= 8)
            {
                ship.LogDamage((int)(ship.OriginalHealth * .5f)); // 50% of ship health
            }
            else
            {
                ship.LogDamage((int)(ship.OriginalHealth * .2f)); // 20% of ship health
            }

            
        }
    }
}