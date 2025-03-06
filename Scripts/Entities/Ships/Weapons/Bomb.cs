
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Bomb : Weapon
    {

        
        protected override List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            //List<Ship> queue = new List<Ship>();
            //if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            //{
            //    List<Ship> enemyShips = Ship.Squad.GetCommand().EnemySquad.GetShips().ToList(); // The ToList() is necessary to prevent alteration to the enemy ships
            //    if (enemyShips.Count > 0)
            //    {
            //        //Debug.Log($"Enemy squad {Ship.Squad.GetCommand().Enemy.Name} has {enemyShips.Count} ships");
            //        queue = enemyShips;
            //    }
            //    else
            //    {
            //        //Debug.Log($"Enemy squad {Ship.Squad.GetCommand().Enemy.Name} has NO ({enemyShips.Count}) ships");
            //        queue = Level.State.GetAllEnemyShips(Side);
            //    }
            //}
            //else
            //{
            //    //Debug.Log($"Either the Squad has no enemy: {Ship.Squad.HasEnemy} or the squad is not attacking: {Ship.Squad.IsAttacking}");
            //}
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                //Debug.Log("Using cached queue");
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            //Debug.Log("Not using cached queue");
            IsUsingCachedTargetingQueue = false;
            //return queue;

            return Ship.Squad.GetCommand().EnemySquad.GetShips().ToList();
            //try
            //{
            //    return Ship.Squad.GetCommand().EnemySquad.GetShips().ToList(); // You only get enemy target ships from a Bomb when there's a bombing run and
            //                                                                   // there's only a bombing run if you have an enemy squad with ships
            //}
            //catch(Exception e)
            //{
            //    Debug.Log(Ship);
            //    Debug.Log(Ship.Squad);
            //    Debug.Log(Ship.Squad.GetCommand());
            //    Debug.Log(Ship.Squad.GetCommand().EnemySquad);
            //    Debug.Log(Ship.Squad.GetCommand().EnemySquad.GetShips());
            //    throw e;
            //}

        }

        /// <summary>
        /// Returns true. All ships are valid targets for the bomb.
        /// </summary>
        /// <param name="potentialTargetShip"></param>
        /// <returns></returns>
        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead; // all ships that aren't dead are "within range" and thus valid
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            Level.State.GetShipDamageStatus(Side, targetShip).TotalDamageSentToShip += Power;
            TargetShip = targetShip;
            //Debug.Log($"Setting target ship to {TargetShip.Name} and sending {Power} / {shipDamageStatus.totalDamageSentToShip} damage ");

        }
        /// <summary>
        /// Used to determine a target ship if the sorted target list can't give you a ship back
        /// </summary>
        /// <param name="ships"></param>
        public void SetRandomTarget(List<Ship> ships)
        {
            SetTargetShip(ships[Utilities.RandomInt(ships.Count)]);
        }
    }
}