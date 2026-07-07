using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class MapBorder : Obstacle
    {

        private GameObject _collidingThing;
        private CollisionAsteroid _collisionAsteroid;
        private Ship _collidingShip;
        private Barge _collidingBarge;
        protected void OnTriggerExit2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            _collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (_collidingThing.CompareTag("Obstacle"))
            {
                _collisionAsteroid = _collidingThing.GetComponent<CollisionAsteroid>();
                if (_collisionAsteroid.HasEnteredMap) 
                {
                    //Debug.Log($"{_collisionAsteroid.Name} left the map border and is being killed at {_collisionAsteroid.GetPosition()}");
                    _collisionAsteroid.Kill(true);
                }
                else
                {
                    //Debug.Log($"{_collisionAsteroid.Name} entered the map border at {_collisionAsteroid.GetPosition()}");
                    _collisionAsteroid.HasEnteredMap = true;
                }


            }
        }

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            Debug.Log($"{Name} collided with {_collidingThing.name}");
            if (_collidingThing.CompareTag("Ship"))
            {
                Debug.Log($"{Name} Hit by ship");
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (_collidingShip.HasTargetDirection)
                {
                    Debug.Log($"{Name} hit the map border while moving in a direction");
                    if (_collidingShip.ShipType == ConfigData.ShipTypes.Barge)
                    {
                        _collidingBarge = (Barge)_collidingShip;
                        if (_collidingBarge.IsCharging)
                        {
                            StartCoroutine(_collidingBarge.StopCharge());
                            return;
                        }
                    }
                    _collidingShip.StopMoving("Hit map border");
                }
            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                _collisionAsteroid = _collidingThing.GetComponent<CollisionAsteroid>();
                _collisionAsteroid.HasTouchedMapBorder = true;
                //Debug.Log($"{_collisionAsteroid.Name} has touched the map border");

            }

        }
    }
}