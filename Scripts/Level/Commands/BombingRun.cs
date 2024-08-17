using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class BombingRun : Command
    {

        /// <summary>
        /// Only available to Yellow Jackets, Fireships, and Strikers. Sends all ships straight onto the ships of the squad and back to the carrier if applicable
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            //Debug.Log("Executing bombing run");

            Squad.ClearTargets(); // Clear all old targets before starting the bombing run
            if (CheckIfStrikersAreDefenseless())
            {
                return;
            }

            // this piece of code seems to make the squad move to it's current location for no particular reason

            // check if squad has reached destination and if so, cancel the timer and start over again for the next destination
            //Vector2 destination = GetDestination();
            //if (HasDestination && Squad.HasReachedDestination)
            //{
            //    RemoveDestination(destination);
            //    AddDestination(destination);
            //}
            //destination = GetDestination();
            //Squad.Move(destination);

            // Setup status and damage
            IsAttacking = true;
            Squad.Status = $"Starting bombing run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();

            // loop through all the ships in the bombing squad
            List<Ship> ships = Squad.GetShips();
            foreach (Ship ship in ships)
            {
                //Debug.Log("Looping through all ships in bombing squad");
                if (ship.ShipType == "Striker")
                {
                    Striker striker = (Striker)ship;
                    striker.HasCompletedRun = false;
                    striker.HasDroppedBomb = false;
                    striker.HasReturnedToCarrier = false;
                }
                else if (ship.ShipType == "Yellow Jacket")
                {
                    YellowJacket yellowJacket = (YellowJacket)ship;
                    yellowJacket.HasCompletedRun = false;
                }

                // loop through all the ships in the target squad
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
                //Debug.Log($"{ship.Name} Target ship: {bomb.TargetShip}");
                //Debug.Log("--------------------");
                //ship.MoveToPoint(ship.TargetShips.First().GetPosition()); // Move to the primary target ship
            }
            CommandFrequency = 2;
            InvokeRepeating(nameof(Timer), .1f, CommandFrequency);

        }
        private bool CheckIfStrikersAreDefenseless()
        {
            if (Squad.IsCarrierSquad)
            {
                if (Squad.GetShips().All((s) =>
                {
                    Striker striker = ((Striker)s);
                    return !striker.IsBombReady && striker.Carrier == null;
                }))
                {
                    Squad.BannedStrats.Add("Aggressive");
                    Squad.BannedStrats.Add("Circle");
                    Squad.BannedStrats.Add("Right Swipe");
                    Squad.BannedStrats.Add("Left Swipe");
                    Squad.BannedStrats.Add("In and Out");

                    //Debug.Log("Strikers are defenseless, cancelling bombing run");
                    SetFinalize("Strikers are defenseless, cancelling bombing run");
                    return true;
                }
            }
            return false;
        }
        private bool ShouldShipPursueTarget(Ship ship)
        {
            if (ship.HasTargetEnemyShipToFollow) // if the ship has target ships and they're not all dead
            {
                if (ship.ShipType == "Striker")
                {
                    Striker striker = (Striker)ship;
                    return striker.IsBombReady; // if it's a striker and its bombs are ready
                }
                else
                {
                    return true;
                }
            }
            return false;
        }
        private void SendShipToTarget(Ship ship)
        {
            ship.MoveToPoint(ship.TargetEnemyShipToFollow.GetPosition()); // Move to the primary target ship
        }
        private bool HaveAllShipsFinished(List<Ship> ships)
        {
            return ships.All((ship) => // if all of the ships have completed their run and are either yellow jackets or are strikers who have reloaded or have no carrier
            {
                if (ship.ShipType == "Striker")
                {
                    Striker striker = (Striker)ship;
                    return striker.HasCompletedRun && (striker.HasReturnedToCarrier || !striker.HasCarrier);
                }
                else if (ship.ShipType == "Yellow Jacket")
                {
                    YellowJacket yellowJacket = (YellowJacket)ship;
                    return yellowJacket.HasCompletedRun;
                }
                else if (ship.ShipType == "Fire Ship")
                {
                    return false;
                }
                return true;
            });
        }

        private int _timerLoops;
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                _timerLoops++;
                //Debug.Log("Bombing timer");
                Squad.Status = $"In the middle of bombing run against {EnemySquad.Name}";
                List<Ship> ships = Squad.GetShips();
                List<long> fireShipsToDetonate = new List<long>();
                ships.ForEach((ship) =>
                {
                    if (ShouldShipPursueTarget(ship))
                    {

                        SendShipToTarget(ship);
                        if (Squad.IsHiveMindControlled && ship.ShipType == "Fire Ship" && ship.DistanceToPoint(ship.TargetCoordinates) < (ConfigData.FireShipExplosionSize - 5))
                        {
                            // if you're a fire ship and within detonation distance of your target, detonate
                            Debug.Log($"{ship.Name} is hivemind controlled and on a bombing run and near its target coordinates and so it's going to detonate. ");
                            fireShipsToDetonate.Add(ship.Id);
                        }
                    }
                    else // if you don't have target ships or all of them are dead
                    {
                        Debug.Log($"{ship.Name} should not pursure its target ({ship.TargetEnemyShipToFollow}) and so it's going back to its carrier");
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            //striker.CompleteRun();
                        }
                        else if (ship.ShipType == "Yellow Jacket")
                        {
                            YellowJacket yellowJacket = (YellowJacket)ship;
                            yellowJacket.HasCompletedRun = true;
                        }
                    }
                    if (ship.ShipType == "Striker")
                    {
                        Striker striker = (Striker)ship;
                        striker.ReturnToCarrierIfNecessary();
                    }
                });

                // This is necessary to prevent modifying the list when the fire ship(s) is killed
                for (int i = 0; i < fireShipsToDetonate.Count; i++)
                {
                    ((FireShip)Level.GetState().GetShipById(fireShipsToDetonate[i])).Detonate();

                }


                if (HaveAllShipsFinished(ships))
                {
                    //Debug.Log("Ended bombing run");
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Completed bombing run");

                    foreach (Ship ship in ships)
                    {
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            if (!striker.HasCarrier) // if the striker doesn't have a carrier, return to the last carrier position
                            {
                                ship.MoveToPoint(striker.LastCarrierPosition + ship.OffsetFromCenter); 
                            }
                        }
                    }
                }

                if (!IsCloseToTarget && !EnemySquad.IsDead)
                {
                    if (Squad.DistanceToPoint(EnemySquad.GetPosition()) < 45)
                    {
                        Debug.Log($"{Squad.Name} is on a bombing run and close to {EnemySquad.Name}");
                        CancelInvoke(nameof(Timer));
                        CommandFrequency = .25f;
                        IsCloseToTarget = true;
                        InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                    }

                }
                else if (IsCloseToTarget && !EnemySquad.IsDead && _timerLoops % 4 == 0 && Squad.DistanceToPoint(EnemySquad.GetPosition()) > 90)
                {
                    Debug.Log($"{Squad.Name} is on a bombing run and no longer close to {EnemySquad.Name}");
                    CancelInvoke(nameof(Timer));
                    CommandFrequency = 2f;
                    IsCloseToTarget = false;
                    InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                }
            }
            
        }
    }
}