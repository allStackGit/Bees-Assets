using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public partial class CollisionAsteroid
    {
        public HashSet<Ship> NearbyShips = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public HashSet<Ship> TouchingShips = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public HashSet<Obstacle> NearbyObstacles = new HashSet<Obstacle>(ReferenceIdentityComparer<Obstacle>.Instance);
        public HashSet<CollisionAsteroid> AsteroidsHit = new HashSet<CollisionAsteroid>(ReferenceIdentityComparer<CollisionAsteroid>.Instance);
        /// <summary>
        /// If this asteroid is a shard then it has a shard family of all the other shards that were spawned from the same asteroid and can't collide with each other
        /// </summary>
        public HashSet<CollisionAsteroid> ShardFamily = new HashSet<CollisionAsteroid>(ReferenceIdentityComparer<CollisionAsteroid>.Instance);
        public CollisionAsteroid LastHitAsteroid;

        public void ShipCollision(Ship ship)
        {
            if (NearbyShips.Contains(ship) && Health > 0)
            {
                TouchingShips.Add(ship);

                if (ship.SizeClass < SizeClass)
                {
                    Health -= math.min(ship.OriginalHealth, Health);
                    ship.LogDamage(ship.Health);
                }
                else if (ship.SizeClass == SizeClass)
                {
                    ship.LogDamage(ship.Health);
                    Health = 0;
                }
                else
                {
                    ship.LogDamage(OriginalHealth);
                    Health = 0;
                }
                NearbyShips.Remove(ship);

                if (Health == 0)
                {
                    GotKilledInShipCollision();
                }
                else if (CheckForCrackedSprite())
                {
                    SwitchToCrackedSprite();
                }
            }
            else
            {
                ship.FoundNearbyAsteroid(this);
                NearbyShips.Add(ship);
            }
        }

        public void ObstacleCollision(Obstacle obstacle)
        {
            if (NearbyObstacles.Contains(obstacle) && HasEnteredMap && Health > 0)
            {
                LastHitAsteroid = (CollisionAsteroid)obstacle;
                if (LastHitAsteroid.HasTouchedMapBorder && LastHitAsteroid.Health > 0 && !ShardFamily.Contains(LastHitAsteroid))
                {
                    AsteroidsHit.Add(LastHitAsteroid);
                    if (LastHitAsteroid.AsteroidsHit.Contains(this))
                    {
                        return;
                    }

                    if (LastHitAsteroid.SizeClass < SizeClass)
                    {
                        Health -= math.min(LastHitAsteroid.OriginalHealth, Health);
                        LastHitAsteroid.Health = 0;
                    }
                    else if (LastHitAsteroid.SizeClass == SizeClass)
                    {
                        Health = 0;
                        LastHitAsteroid.Health = 0;
                    }
                    else
                    {
                        LastHitAsteroid.Health -= math.min(OriginalHealth, LastHitAsteroid.Health);
                        Health = 0;
                    }

                    if (LastHitAsteroid.Health == 0)
                    {
                        LastHitAsteroid.LastHitAsteroid = this;
                        LastHitAsteroid.GotKilledInCollision();
                    }
                    else if (LastHitAsteroid.CheckForCrackedSprite())
                    {
                        LastHitAsteroid.SwitchToCrackedSprite();
                    }
                    if (Health == 0)
                    {
                        GotKilledInCollision();
                        LastHitAsteroid = null;
                    }
                    else if (CheckForCrackedSprite())
                    {
                        SwitchToCrackedSprite();
                        LastHitAsteroid = null;
                    }
                    else
                    {
                        LastHitAsteroid = null;
                    }
                }
                else
                {
                    LastHitAsteroid = null;
                }
            }
            else if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
            {
                NearbyObstacles.Add(obstacle);
            }
        }

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private Obstacle _collidingObstacle;

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                ShipCollision(_collidingThing.GetComponent<Ship>());
            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                ObstacleCollision(_collidingThing.GetComponent<Obstacle>());
            }
        }

        private void OnTriggerExit2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (TouchingShips.Contains(_collidingShip))
                {
                    TouchingShips.Remove(_collidingShip);
                }
                else if (NearbyShips.Contains(_collidingShip))
                {
                    NearbyShips.Remove(_collidingShip);
                    _collidingShip.LeftNearbyAsteroid(this);
                }
            }
            else if (_collidingThing.CompareTag("Obstacle"))
            {
                _collidingObstacle = _collidingThing.GetComponent<Obstacle>();
                if (NearbyObstacles.Contains(_collidingObstacle))
                {
                    NearbyObstacles.Remove(_collidingObstacle);
                }
            }
        }
    }
}
