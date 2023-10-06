
using Assets.Scripts.Level;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Bomb : Weapon
    {
        protected override List<Ship> GetPotentialEnemyTargetShips()
        {
            List<Ship> queue = new List<Ship>();
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                List<Ship> enemyShips = Ship.Squad.Command.Enemy.GetShips();
                if (enemyShips.Count > 0)
                {
                    queue = enemyShips;
                }
                else
                {
                    queue = Level.GetState().GetAllEnemyShips(Side);
                }
            }
            if (CachedShootingStrategy == Ship.ShootingStrategy && queue.Count == CachedTargetingQueue.Count)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            IsUsingCachedTargetingQueue = false;
            return queue;
        }

        protected override bool CheckIfShipIsValidTarget(Ship potentialTargetShip)
        {
            return true; // all ships are "within range" and thus valid
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            //Debugger.Log($"Setting target ship to {targetShip.Name}");
            ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(targetShip);
            shipDamageStatus.totalDamageSentToShip += Power;
            TargetShip = targetShip;
        }
    }
}