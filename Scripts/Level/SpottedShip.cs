using Assets.Scripts.Entities.Ships;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Level
{
    public class SpottedShip
    {
        public Ship Ship;
        public long SpotterId;

        public SpottedShip(Ship ship, long spotterId)
        {
            this.Ship = ship;
            this.SpotterId = spotterId;
        }
    }
}