

using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Level

{
    /// <summary>
    /// Basic class that keeps track of the initial health of a ship 
    /// and the damage sent towards the ship by enemy projectiles.
    /// The damage sent does not necessarily match damage received
    /// </summary>
    public class ShipDamageStatus
    {
        public Ship Ship;
        public int Health, TotalDamageSentToShip;

        public ShipDamageStatus(Ship ship)
        {
            Ship = ship;
            Health = Ship.Health;
        }
    }
}