using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private readonly RaycastHit2D[] _obstacleCastHits = new RaycastHit2D[16];
        private RaycastHit2D _movementObstacleHit;
        private int _rangeIndex;
        private List<Ship> _tempShips;
        private Vector2 _randomPointBounds, _basePosition, _randomPoint;
        private int _x, _y;
        private float _halfWidth, _halfHeight;
        private int _clearance;

        public static RaycastHit2D BoxCastDebug(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance, int mask)
        {
            RaycastHit2D hit = Physics2D.BoxCast(origin, size, angle, direction, distance, mask);
            Debug.Log($"{hit}, {hit.collider}, {hit.transform}");
            return hit;
        }

        public bool HasObstacleInPath(Vector2 destination)
        {
            return GetObstacleInPath(destination) != null;
        }

        public Collider2D GetObstacleInPath(Vector2 destination)
        {
            return GetLiveObstacleFromBoxCast(
                GetPosition(),
                GetSize(),
                GetDegreesTowardsPoint(destination),
                -DirectionToPoint(destination),
                DistanceToPoint(destination));
        }

        private Collider2D GetLiveObstacleFromBoxCast(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance)
        {
            return GetLiveObstacleFromBoxCast(origin, size, angle, direction, distance, out _movementObstacleHit);
        }

        private Collider2D GetLiveObstacleFromBoxCast(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance, out RaycastHit2D liveHit)
        {
            liveHit = new RaycastHit2D();
            Collider2D nearestCollider = null;
            float nearestDistance = float.MaxValue;
            int hitCount = Physics2D.BoxCastNonAlloc(origin, size, angle, direction, _obstacleCastHits, distance, ConfigData.ObstaclesLayerMask);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit2D hit = _obstacleCastHits[i];
                Collider2D hitCollider = hit.collider;
                if (hitCollider == null)
                {
                    continue;
                }

                Obstacle obstacle = hitCollider.GetComponent<Obstacle>() ?? hitCollider.GetComponentInParent<Obstacle>();
                if (ShouldAvoidObstacle(obstacle) && hit.distance < nearestDistance)
                {
                    nearestDistance = hit.distance;
                    liveHit = hit;
                    nearestCollider = hitCollider;
                }
            }
            return nearestCollider;
        }

        private static bool ShouldAvoidObstacle(Obstacle obstacle)
        {
            return obstacle != null &&
                   !obstacle.IsDead &&
                   (obstacle.ObstacleType == ConfigData.ObstacleTypes.StaticObstacle ||
                    obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid ||
                    obstacle.ObstacleType == ConfigData.ObstacleTypes.AsteroidPiece);
        }

        public bool IsShipWithinRange(Ship ship)
        {
            for (_rangeIndex = 0; _rangeIndex < Weapons.Count; _rangeIndex++)
            {
                if (Weapons[_rangeIndex].IsShipValidTarget(ship))
                {
                    return true;
                }
            }
            return false;
        }

        public bool CanSeeShip(Ship ship)
        {
            return Sight > 0 ? DistanceTo(ship) <= Sight : IsShipWithinRange(ship);
        }

        public bool IsAnySquadShipWithinRange(Squad enemy)
        {
            _tempShips = enemy.GetShips();
            for (_rangeIndex = 0; _rangeIndex < _tempShips.Count; _rangeIndex++)
            {
                if (IsShipWithinRange(_tempShips[_rangeIndex]))
                {
                    return true;
                }
            }
            return false;
        }

        public bool AreAllSquadShipsWithinRange(Squad squad)
        {
            return squad.GetShips().All(IsShipWithinRange);
        }

        public Vector2 GetSize() => _size;
        public float GetWidth() => _size.x;
        public float GetHalfWidth() => GetWidth() / 2;
        public float GetHeight() => _size.y;
        public float GetHalfHeight() => GetHeight() / 2;
        public Vector2 GetLeftMostPoint() => new Vector2(GetX() - GetHalfWidth(), GetY());
        public Vector2 GetRightMostPoint() => new Vector2(GetX() + GetHalfWidth(), GetY());
        public Vector2 GetTopMostPoint() => new Vector2(GetX(), GetY() + GetHalfHeight());
        public Vector2 GetBottomMostPoint() => new Vector2(GetX(), GetY() - GetHalfHeight());

        public Vector2 GetRandomPointOnShip(Vector2 nearPosition)
        {
            if (SizeClass == 1)
            {
                return GetPosition();
            }

            _halfWidth = GetHalfWidth() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ShipType);
            _halfHeight = GetHalfHeight() - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ShipType);
            _randomPointBounds = Utilities.ForceBounds(10, 10, _halfWidth, _halfHeight, -_halfWidth, -_halfHeight);
            _basePosition = nearPosition + Level.GetPosition();
            _randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, _randomPointBounds, Vector2.zero) + _basePosition;

            int attempts = 0;
            while (!Collider.OverlapPoint(_randomPoint) && attempts < 20)
            {
                _randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, _randomPointBounds, Vector2.zero) + _basePosition;
                attempts++;
            }

            if (attempts == 20)
            {
                for (_x = (int)-_halfWidth; _x < _halfWidth; _x++)
                {
                    for (_y = (int)-_halfHeight; _y < _halfHeight; _y++)
                    {
                        _randomPoint = _basePosition + new Vector2(_x, _y);
                        if (Collider.OverlapPoint(_randomPoint))
                        {
                            return _randomPoint;
                        }
                    }
                }
                Debug.Log($"Could not find a random point on {Name}");
            }
            return _randomPoint;
        }

        public int GetClearance()
        {
            _clearance = Clearance;
            if (_clearance == 0)
            {
                Level.CalculateShipClearances();
                _clearance = Stage.ShipClearances.GetValueOrDefault(ShipType);
            }
            return _clearance;
        }

        public bool IsInBounds()
        {
            if (!_isInBounds)
            {
                _isInBounds = GetPosition() == Level.ForceBounds(GetPosition());
            }
            return _isInBounds;
        }

        public override string ToString()
        {
            return $"{Name} IsDead? {IsDead}";
        }

        public static double GetAverageHealthPercent(List<Ship> ships)
        {
            if (ships == null || ships.Count == 0)
            {
                return 0;
            }

            double total = 0;
            foreach (Ship ship in ships)
            {
                if (ship.OriginalHealth > 0)
                {
                    total += (double)ship.Health / ship.OriginalHealth;
                }
            }
            return Math.Round((total / ships.Count) * 100);
        }
    }
}
