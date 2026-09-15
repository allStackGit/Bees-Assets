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
        private bool IsTraining => Stage != null && Stage.IsTraining;

        protected void OnTriggerExit2D(Collider2D collider)
        {
            if (collider == null)
            {
                return;
            }

            //Debug.Log($"{Name} collided");
            _collidingThing = collider.gameObject;
            //Debug.Log($"Projectile #{Id} collided with {collidingThing.name} at {Level.Updates} updates");
            if (_collidingThing.CompareTag("Obstacle"))
            {
                _collisionAsteroid = _collidingThing.GetComponent<CollisionAsteroid>() ??
                    _collidingThing.GetComponentInParent<CollisionAsteroid>();
                if (_collisionAsteroid == null)
                {
                    return;
                }

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
            if (collider == null)
            {
                return;
            }

            _collidingThing = collider.gameObject;
            if (!IsTraining)
            {
                Debug.Log($"{Name} collided with {_collidingThing.name}");
            }
            if (_collidingThing.CompareTag("Ship"))
            {
                if (!IsTraining)
                {
                    Debug.Log($"{Name} Hit by ship");
                }

                // Some ship prefabs expose a tagged child collider rather than putting every
                // collider on the Ship component's GameObject. Resolve through the hierarchy so a
                // valid ship collision cannot turn into a null dereference at the border.
                _collidingShip = _collidingThing.GetComponent<Ship>() ??
                    _collidingThing.GetComponentInParent<Ship>();
                if (_collidingShip == null)
                {
                    return;
                }

                // Scripted exits deliberately opt out of the playable-map clamp. Do not stop those
                // ships at the border. Keep a cutscene camera attached while a scripted ship exits;
                // the owning sequence decides when the follow ends after the ship has disappeared.
                if (_collidingShip.CanOverrideBounds)
                {
                    return;
                }

                if (_collidingShip.HasTargetDirection)
                {
                    if (!IsTraining)
                    {
                        Debug.Log($"{Name} hit the map border while moving in a direction");
                    }
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
                _collisionAsteroid = _collidingThing.GetComponent<CollisionAsteroid>() ??
                    _collidingThing.GetComponentInParent<CollisionAsteroid>();
                if (_collisionAsteroid == null)
                {
                    return;
                }
                _collisionAsteroid.HasTouchedMapBorder = true;
                //Debug.Log($"{Name} has touched map border");

            }

        }
    }
}