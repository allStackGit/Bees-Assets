using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
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
            Squad.Status = $"Starting charging run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();

            List<Ship> ships = Squad.GetShips();
            foreach (Ship ship in ships)
            {

                // loop through all the ships in the target squad
                if (ship.ShipType == "Barge")
                {
                    ((Barge)ship).HasCompletedRun = false;
                }
                GetTargetShip(ship);
            }

            InvokeRepeating(nameof(Timer), 0, CommandFrequency);

        }
        private void GetTargetShip(Ship ship)
        {
            Bomb bomb = (Bomb)ship.Weapons.First();
            int loops = 0;
            while (!bomb.DetermineTargetShip(bomb.MakeSortedTargetingList(true), true) && loops < 10)
            {
                Squad.DamageSentToEnemyShipsBySquad.Clear();
                loops++;
            }
            ship.TargetEnemyShipToFollow = bomb.TargetShip;
            if (loops == 10)
            {
                Debug.Log($"Looped 10 times while trying to determine a target ship for {bomb.Name}");
            }
        } 
        private void SendShipToTarget(Ship ship)
        {
            ship.MoveToDirectionOfPoint(ship.SetAndGetTargetEnemy().GetPosition()); // Move to the primary target ship
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
            return !ship.HasStartedCharging && !ship.HasCompletedRun && ship.HasTargetEnemyShipToFollow;
        }

        private bool HasTargetsWithinChargingRange(Ship ship)
        {
            return ship.HasWeaponsTargetShips && ship.WeaponsTargetShips.Any((targetShip) => targetShip != null &&  ship.ShipsWithinRange.Contains(targetShip) && Utilities.IsRotatedTowards(ship.gameObject, ship.GetDegreesTowardsPoint(targetShip.GetPosition())));
        }



        public IEnumerator ChargeTarget(Barge barge)
        {
            Ship target = barge.WeaponsTargetShips.First();
            barge.OriginalPower = barge.Charge.Power;

            barge.StopMoving("Pausing to build up steam before charging");
            Debug.Log($"{barge.Name} is about to charge {target.Name}");

            yield return new WaitForSeconds(2);

            if (!barge.IsDead && target != null && !target.IsDead)
            {
                Debug.Log($"Charging!");
                barge.IsCharging = true;
                barge.SetCurrentSpeed(80, 80);
                barge.MoveToDirectionOfPoint(target.GetPosition());
            }



            yield return new WaitForSeconds(1);
            barge.StopCharge();

            //yield return new WaitForSeconds(1);
            //Debug.Log("1 second");
            //yield return new WaitForSeconds(1);
            //Debug.Log("2 seconds");
            //yield return new WaitForSeconds(1);
            //Debug.Log("3 seconds");
            //yield return new WaitForSeconds(1);
            //Debug.Log("4 seconds");
            //yield return new WaitForSeconds(1);
            yield return new WaitForSeconds(5);

            barge.FinishCoolDown();

        }


        private void Timer()
        {
            if (!Squad.IsDead)
            {
                //Debug.Log("Bombing timer");
                Squad.Status = $"In the middle of charging run against {EnemySquad.Name}";
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
                                StartCoroutine(ChargeTarget(ship));
                            }
                        }
                        else
                        {
                            SendShipToTarget(ship);
                        }
                    }else if (!ship.HasWeaponsTargetShips)
                    {
                        GetTargetShip(ship);
                    }
                    //else // if you don't have target ships or all of them are dead
                    //{
                    //    Debug.Log($"{ship.Name} should not pursure targets because either it is charging ({ship.IsCharging}), or does not have target ships that aren't null {(ship.HasTargetShips && ship.TargetShips.Any((targetShip) => targetShip != null))}");
                    //    if (!ship.IsCharging)
                    //    {
                    //        ship.HasCompletedRun = true;
                    //    }
                    //}
                });


                if (HaveAllShipsFinished(ships))
                {
                    //Debug.Log("Ended charging run");
                    SetFinalize("Completed charging run");

                }
            }

        }
    }
}