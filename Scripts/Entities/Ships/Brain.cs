using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Brain : MonoBehaviour
    {
        public Ship Ship, Enemy;
        public float ShipType;
        public int SpottedShipIndex;
        public List<Ship> SpottedShips;
        public List<Ship> Ships;
        public float[] BlankObservation = new float[5];
        public List<SpottedShip> Filtered;
        public SpottedShip SpottedShip;
        public Ship LocalShip;
        public Vector2 NormalizedPosition;
        public bool Contains;
        public int SpottedShipCount, i, ss;

        public long Id;

        public void Setup(Ship ship)
        {
            Ship = ship;
            Id = Utilities.Hash();
            ShipType = (float)Ship.ShipTypeLetter;
            SpottedShipIndex = Ship.Side - 1;
        }
    }
}
