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
        /*
         Only available to Yellow Jackets and Strikers. Sends all ships straight onto the ships of the squad and back to the carrier if applicable
         */
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            //Debugger.Log("Executing bombing run");

            if (Squad != null && !Squad.IsDead)
            {
                Squad.ClearTargets(); // Clear all old targets before starting the bombing run
                CheckIfStrikersAreDefenseless();

                // check if squad has reached destination and if so, cancel the timer and start over again for the next destination
                Vector2 destination = GetDestination();
                if (Squad.HasReachedDestination)
                {
                    RemoveDestination(destination);
                    AddDestination(destination);
                }
                destination = GetDestination();
                Squad.Move(destination);
                IsAttacking = true;


                // loop through all the ships in the bombing squad
                Squad.Status = $"Starting bombing run against {Enemy.Name}";
                PrepareDamageToSendEntries();

                List<Ship> ships = Squad.GetShips();
                foreach (Ship ship in ships)
                {
                    //Debugger.Log("Looping through all ships in bombing squad");
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
                    while (!bomb.DetermineTargetShip(bomb.MakeTargetingQueue(), true))
                    {
                        Squad.DamageSentToEnemyShipsBySquad.Clear();
                    }
                    //Debugger.Log($"Target ship: {bomb.TargetShip}");
                    //Debugger.Log("--------------------");
                }

                InvokeRepeating(nameof(Timer), .01f, .5f);
            }
            else
            {
                SetFinalize("The squad is dead");
            }
            
        }
        private void CheckIfStrikersAreDefenseless()
        {
            if (Squad.IsCarrierSquad)
            {
                if (Squad.GetShips().All((s) =>
                {
                    Striker striker = ((Striker)s);
                    return !striker.AreBombsReady && (striker.Carrier == null || striker.Carrier.IsDead);
                }))
                {
                    Squad.BannedStrats.Add("Aggressive");
                    Squad.BannedStrats.Add("Circle");
                    Squad.BannedStrats.Add("Right Swipe");
                    Squad.BannedStrats.Add("Left Swipe");
                    Squad.BannedStrats.Add("In and Out");

                    //Debugger.Log("Strikers are defenseless, cancelling bombing run");
                    SetFinalize("Strikers are defenseless, cancelling bombing run");
                }
            }
        }
        private bool ShouldShipPursueTarget(Ship ship)
        {
            if (ship.HasTargetShips && !ship.TargetShips.All((ship) => ship.IsDead)) // if the ship has target ships and they're not all dead
            {
                if (ship.ShipType == "Striker")
                {
                    Striker striker = (Striker)ship;
                    return !striker.HasDroppedBomb; // if it's a striker and it has dropped bombs then it shouldn't pursue;
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
            ship.MoveToPoint(ship.TargetShips.First().GetPosition()); // Move to the primary target ship
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
                return true;
            });
        }

        private void Timer()
        {
            if (Squad != null && !Squad.IsDead)
            {
                //Debugger.Log("Bombing timer");
                Squad.Status = $"In the middle of bombing run against {Enemy.Name}";
                List<Ship> ships = Squad.GetShips();
                for (int i = 0; i < ships.Count; i++)
                {
                    Ship ship = ships[i];
                    if (ShouldShipPursueTarget(ship)) 
                    {

                        SendShipToTarget(ship);
                        if (ship.ShipType == "Fire Ship" && ship.DistanceToPoint(ship.TargetCoordinates) < (ConfigData.FireShipExplosionSize - 5))
                        {
                            ((FireShip)ship).Detonate(); // if you're a fire ship and within detonation distance of your target, detonate
                        }
                    }
                    else // if you don't have target ships or all of them are dead
                    {
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            striker.HasCompletedRun = true;
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
                }
                

                if (HaveAllShipsFinished(ships))
                {
                    //Debugger.Log("Ended bombing run");
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Completed bombing run");

                    foreach (Ship ship in ships)
                    {
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            if (!striker.HasCarrier) // if the striker doesn't have a carrier, return to the last carrier position
                            {
                                Vector2 destination = striker.LastCarrierPosition;
                                ship.MoveToPoint(Level.ForceBounds(destination + ship.OffsetFromCenter)); 
                            }
                        }
                    }
                }
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }
            
        }
    }
}