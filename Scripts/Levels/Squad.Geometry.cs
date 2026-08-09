using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        private List<Ship> _squadShips;
        private float _width, _height, _midX, _midY;

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
            return enemy != null && GetShips().Any(ship => ship.IsAnySquadShipWithinRange(enemy));
        }

        public bool IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return squad != null && GetShips().Any(ship => ship.AreAllSquadShipsWithinRange(squad));
        }

        public bool AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return squad != null && GetShips().All(ship => ship.AreAllSquadShipsWithinRange(squad));
        }

        public bool AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return squad != null && GetShips().All(ship => ship.IsAnySquadShipWithinRange(squad));
        }

        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }

        public Vector2 GetPosition() => GetCenterPoint();

        public Vector2 GetLeftMostPoint()
        {
            _tempShip = GetShips().OrderBy(ship => ship.GetLeftMostPoint().x).First();
            return new Vector2(_tempShip.GetLeftMostPoint().x, _tempShip.GetY());
        }

        public Vector2 GetRightMostPoint()
        {
            _tempShip = GetShips().OrderByDescending(ship => ship.GetRightMostPoint().x).First();
            return new Vector2(_tempShip.GetRightMostPoint().x, _tempShip.GetY());
        }

        public Vector2 GetTopMostPoint()
        {
            _tempShip = GetShips().OrderByDescending(ship => ship.GetTopMostPoint().y).First();
            return new Vector2(_tempShip.GetX(), _tempShip.GetTopMostPoint().y);
        }

        public Vector2 GetBottomMostPoint()
        {
            _tempShip = GetShips().OrderBy(ship => ship.GetBottomMostPoint().y).First();
            return new Vector2(_tempShip.GetX(), _tempShip.GetBottomMostPoint().y);
        }

        public float GetWidth() => Math.Abs(GetLeftMostPoint().x - GetRightMostPoint().x);
        public float GetHeight() => Math.Abs(GetTopMostPoint().y - GetBottomMostPoint().y);

        public Vector2 GetCenterPoint()
        {
            _width = GetWidth();
            _height = GetHeight();
            _midX = GetRightMostPoint().x - (_width / 2);
            _midY = GetBottomMostPoint().y + (_height / 2);
            return new Vector2(_midX, _midY);
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