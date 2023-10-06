

using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Level

{
    /*
     * Basic class that keeps track of the initial health of a ship 
     * and the damage sent towards the ship by enemy projectiles.
     * 
     * The damage sent does not necessarily match damage received
     */
    public class ShipDamageStatus
    {
        public Ship ship;
        public int health, totalDamageSentToShip;

        public ShipDamageStatus(Ship ship)
        {
            this.ship = ship;
            health = ship.Health;
            totalDamageSentToShip = 0;
        }
    }
}