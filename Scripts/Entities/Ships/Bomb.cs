
using Assets.Scripts.Level;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
                List<Ship> enemyShips = Ship.Squad.Command.Enemy.GetShips().ToList(); // The ToList() is necessary to prevent alternation to the enemy ships
                if (enemyShips.Count > 0)
                {
                    //Debugger.Log($"Enemy squad {Ship.Squad.Command.Enemy.Name} has {enemyShips.Count} ships");
                    queue = enemyShips;
                }
                else
                {
                    //Debugger.Log($"Enemy squad {Ship.Squad.Command.Enemy.Name} has NO ({enemyShips.Count}) ships");
                    queue = Level.GetState().GetAllEnemyShips(Side);
                }
            }
            else
            {
                //Debugger.Log($"Either the Squad has no enemy: {Ship.Squad.HasEnemy} or the squad is not attacking: {Ship.Squad.IsAttacking}");
            }
            if (CachedShootingStrategy == Ship.ShootingStrategy && queue.Count == CachedTargetingQueue.Count)
            {
                //Debugger.Log("Using cached queue");
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            //Debugger.Log("Not using cached queue");
            IsUsingCachedTargetingQueue = false;
            return queue;
        }

        protected override bool CheckIfShipIsValidTarget(Ship potentialTargetShip)
        {
            return true; // all ships are "within range" and thus valid
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(targetShip);
            shipDamageStatus.totalDamageSentToShip += Power;
            TargetShip = targetShip;
            //Debugger.Log($"Setting target ship to {TargetShip.Name} and sending {Power} / {shipDamageStatus.totalDamageSentToShip} damage ");

        }
    }
}