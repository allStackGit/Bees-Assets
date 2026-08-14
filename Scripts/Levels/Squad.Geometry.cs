using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        private List<Ship> _squadShips;

        public bool CanSeeSquad(Squad squad)
        {
            if (squad == null || squad.IsDead)
            {
                return false;
            }

            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                _squadShips = squad.GetShips();
                foreach (Ship squadShip in _squadShips)
                {
                    if (ship.CanSeeShip(squadShip))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(Squad enemy)
        {
            if (enemy == null)
            {
                return false;
            }
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].IsAnySquadShipWithinRange(enemy))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            if (squad == null)
            {
                return false;
            }
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].AreAllSquadShipsWithinRange(squad))
                {
                    return true;
                }
            }
            return false;
        }

        public bool AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            if (squad == null)
            {
                return false;
            }
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (!ships[i].AreAllSquadShipsWithinRange(squad))
                {
                    return false;
                }
            }
            return true;
        }

        public bool AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            if (squad == null)
            {
                return false;
            }
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (!ships[i].IsAnySquadShipWithinRange(squad))
                {
                    return false;
                }
            }
            return true;
        }

        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }

        public Vector2 GetPosition() => GetCenterPoint();

        public Vector2 GetLeftMostPoint()
        {
            return TryGetBounds(out Vector2 left, out _, out _, out _) ? left : Vector2.zero;
        }

        public Vector2 GetRightMostPoint()
        {
            return TryGetBounds(out _, out Vector2 right, out _, out _) ? right : Vector2.zero;
        }

        public Vector2 GetTopMostPoint()
        {
            return TryGetBounds(out _, out _, out Vector2 top, out _) ? top : Vector2.zero;
        }

        public Vector2 GetBottomMostPoint()
        {
            return TryGetBounds(out _, out _, out _, out Vector2 bottom) ? bottom : Vector2.zero;
        }

        public float GetWidth()
        {
            return TryGetBounds(out Vector2 left, out Vector2 right, out _, out _)
                ? right.x - left.x
                : 0f;
        }

        public float GetHeight()
        {
            return TryGetBounds(out _, out _, out Vector2 top, out Vector2 bottom)
                ? top.y - bottom.y
                : 0f;
        }

        public Vector2 GetCenterPoint()
        {
            if (!TryGetBounds(out Vector2 left, out Vector2 right, out Vector2 top, out Vector2 bottom))
            {
                return Vector2.zero;
            }

            return new Vector2((left.x + right.x) * 0.5f, (bottom.y + top.y) * 0.5f);
        }

        private bool TryGetBounds(out Vector2 left, out Vector2 right, out Vector2 top, out Vector2 bottom)
        {
            List<Ship> ships = GetShips();
            if (ships == null || ships.Count == 0)
            {
                left = right = top = bottom = Vector2.zero;
                return false;
            }

            Ship first = ships[0];
            left = first.GetLeftMostPoint();
            right = first.GetRightMostPoint();
            top = first.GetTopMostPoint();
            bottom = first.GetBottomMostPoint();

            for (int i = 1; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                Vector2 candidate = ship.GetLeftMostPoint();
                if (candidate.x < left.x)
                {
                    left = candidate;
                }

                candidate = ship.GetRightMostPoint();
                if (candidate.x > right.x)
                {
                    right = candidate;
                }

                candidate = ship.GetTopMostPoint();
                if (candidate.y > top.y)
                {
                    top = candidate;
                }

                candidate = ship.GetBottomMostPoint();
                if (candidate.y < bottom.y)
                {
                    bottom = candidate;
                }
            }
            return true;
        }

        public float AngleToPoint(Vector2 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }

        public Vector2 CirclePoint(float angle, float distance)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            _tempPosition = GetPosition();
            return new Vector2(
                _tempPosition.x + (Mathf.Cos(angle) * distance),
                _tempPosition.y + (Mathf.Sin(angle) * distance));
        }
    }
}