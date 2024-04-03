using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Charge : Command
    {
        public HashSet<Ship> ChargingShips = new HashSet<Ship>();
        public bool IsCharging;
        /// <summary>
        ///  Sends the squad towards the enemy and follows them, when the ship is close enough, it pauses to build up "steam" and then charges forward, ramming the ship(s) in front
        ///  and damaging them. The ship takes damage from the charge even if it doesn't hit another ship. Currently only works for squads of barges
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            //Debug.Log("Executing bombing run");

            IsAttacking = true;


            // loop through all the ships in the bombing squad
            Squad.Status = $"Starting charging run against {Enemy.Name}";
            PrepareDamageToSendEntries();

            List<Ship> ships = Squad.GetShips();
            foreach (Ship ship in ships)
            {

                // loop through all the ships in the target squad
                if (ship.ShipType == "Barge")
                {
                    ((Barge)ship).HasCompletedRun = false;
                }
                Bomb bomb = (Bomb)ship.Weapons.First();
                int loops = 0;
                while (!bomb.DetermineTargetShip(bomb.MakeTargetingQueue(true), true) && loops < 10)
                {
                    Squad.DamageSentToEnemyShipsBySquad.Clear();
                    loops++;
                }
            }

            InvokeRepeating(nameof(Timer), .1f, ConfigData.CommandTimerFrequency);

        }
        private void SendShipToTarget(Ship ship)
        {
            ship.MoveToDirection(ship.TargetShips.First().GetPosition()); // Move to the primary target ship
        }
        private bool HaveAllShipsFinished(List<Barge> ships)
        {
            return ships.All((ship) =>
            {
                return ship.HasCompletedRun;
            });
        }
        private bool ShouldShipPursueTarget(Barge ship)
        {
            return !ship.IsCharging && ship.HasTargetShips && ship.TargetShips.Any((targetShip) => targetShip != null);
        }

        private bool HasTargetsWithinChargingRange(Ship ship)
        {
            return ship.HasTargetShips && ship.TargetShips.Any((targetShip) => targetShip != null &&  ship.ShipsWithinRange.Contains(targetShip) && Utilities.IsRotatedTowards(ship.gameObject, ship.GetDegreesTowardsPoint(targetShip.GetPosition())));
        }

        




        private void Timer()
        {
            if (!Squad.IsDead)
            {
                //Debug.Log("Bombing timer");
                Squad.Status = $"In the middle of charging run against {Enemy.Name}";
                List<Barge> ships = Squad.GetShips().Select((ship) => (Barge)ship).ToList();
                ships.ForEach((ship) =>
                {
                    if (ShouldShipPursueTarget(ship))
                    {
                        if (HasTargetsWithinChargingRange(ship))
                        {
                            if (!ChargingShips.Contains(ship))
                            {
                                ChargingShips.Add(ship);
                                IsCharging = true;
                                StartCoroutine(ship.ChargeTarget());
                            }
                        }
                        else
                        {
                            SendShipToTarget(ship);
                        }
                    }
                    else // if you don't have target ships or all of them are dead
                    {
                        if (!ship.IsCharging)
                        {
                            ship.HasCompletedRun = true;
                        }
                    }
                });


                if (HaveAllShipsFinished(ships))
                {
                    Debug.Log("Ended bombing run");
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Completed charging run");

                }
            }

        }
    }
}