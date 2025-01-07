
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Level;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Bomb : Weapon
    {
        protected override List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            List<Ship> queue = new List<Ship>();
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                List<Ship> enemyShips = Ship.Squad.Command.EnemySquad.GetShips().ToList(); // The ToList() is necessary to prevent alteration to the enemy ships
                if (enemyShips.Count > 0)
                {
                    //Debug.Log($"Enemy squad {Ship.Squad.Command.Enemy.Name} has {enemyShips.Count} ships");
                    queue = enemyShips;
                }
                else
                {
                    //Debug.Log($"Enemy squad {Ship.Squad.Command.Enemy.Name} has NO ({enemyShips.Count}) ships");
                    queue = Level.GetState().GetAllEnemyShips(Side);
                }
            }
            else
            {
                //Debug.Log($"Either the Squad has no enemy: {Ship.Squad.HasEnemy} or the squad is not attacking: {Ship.Squad.IsAttacking}");
            }
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                //Debug.Log("Using cached queue");
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            //Debug.Log("Not using cached queue");
            IsUsingCachedTargetingQueue = false;
            return queue;
        }

        /// <summary>
        /// Returns true. All ships are valid targets for the bomb.
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return true; // all ships are "within range" and thus valid
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(targetShip);
            shipDamageStatus.TotalDamageSentToShip += Power;
            TargetShip = targetShip;
            //Debug.Log($"Setting target ship to {TargetShip.Name} and sending {Power} / {shipDamageStatus.totalDamageSentToShip} damage ");

        }
    }
}