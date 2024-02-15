using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
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

            if (Squad != null && !Squad.IsDead)
            {
                if (Squad.IsCarrierSquad)
                {
                    if (Squad.GetShips().All((s) =>
                    {
                        Striker striker = ((Striker)s);
                        return !striker.BombsReady && (striker.Carrier == null || striker.Carrier.IsDead);
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
                //List<Ship> chosenTargets = new List<Ship>();
                List<Ship> ships = Squad.GetShips();
                foreach (Ship ship in ships)
                {
                    if (ship.ShipType == "Striker")
                    {
                        Striker striker = (Striker)ship;
                        striker.CompletedRun = false;
                        if (!striker.BombsReady)
                        {
                            return; // skip to next ship
                        }
                    }
                    else if (ship.ShipType == "Yellow Jacket")
                    {
                        YellowJacket yellowJacket = (YellowJacket)ship;
                        yellowJacket.CompletedRun = false;
                    }

                    // loop through all the ships in the target squad
                    Bomb bomb = (Bomb)ship.Weapons.First();
                    while (!bomb.DetermineTargetShip(bomb.MakeTargetingQueue(), true))
                    {
                        Squad.DamageSentToEnemyShipsBySquad.Clear();
                    }
                    //Debugger.Log($"Target ship: {bomb.TargetShip}");
                }

                InvokeRepeating(nameof(Timer), .01f, .5f);
            }
            else
            {
                SetFinalize("The squad is dead");
            }
            
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
                    if (ship.HasTargetShips && !ship.TargetShips.All((ship) => ship.IsDead))
                    {
                        ship.MoveToPoint(ship.TargetShips.First().GetPosition());

                        if (ship.ShipType == "Fire Ship" && ship.DistanceToPoint(ship.TargetCoordinates) < 30)
                        {
                            ((FireShip)ship).Detonate();
                        }
                    }
                    else
                    {
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            striker.CompletedRun = true;
                        }
                        else if (ship.ShipType == "Yellow Jacket")
                        {
                            YellowJacket yellowJacket = (YellowJacket)ship;
                            yellowJacket.CompletedRun = true;
                        }
                    }
                    if (ship.ShipType == "Striker")
                    {
                        ReturnToCarrier(ship);
                    }
                }
                

                if (ships.All((ship) =>
                {
                    if (ship.ShipType == "Striker")
                    {
                        Striker striker = (Striker)ship;
                        return striker.CompletedRun && (striker.BombsReady || !striker.HasCarrier);
                    }
                    else if (ship.ShipType == "Yellow Jacket")
                    {
                        YellowJacket yellowJacket = (YellowJacket)ship;
                        return yellowJacket.CompletedRun;
                    }
                    return true;
                }))
                {
                    //Debugger.Log("Ended bombing run");
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Completed bombing run");

                    foreach (Ship ship in ships)
                    {
                        if (ship.ShipType == "Striker")
                        {
                            Striker striker = (Striker)ship;
                            if (!striker.HasCarrier)
                            {
                                Vector2 destination = striker.LastCarrierPosition;

                                float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
                                float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);
                                ship.MoveToPoint(new Vector2(x, y));
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

        private void ReturnToCarrier(Ship ship)
        {
            Striker striker = (Striker)ship;
            if (!striker.BombsReady || striker.CompletedRun)
            {
                striker.CompletedRun = true;
                // send any bombers that aren't loaded to their carrier
                //Debugger.Log($"Sending {striker.Id} back to its carrier");
                striker.Bomb.TargetShip = null;
                if (striker.HasCarrier)
                {
                    Vector2 destination = striker.Carrier.GetPosition();

                    float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
                    float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);

                    Vector2 targetPoint = new Vector2(x, y);
                    float distance = striker.DistanceToPoint(targetPoint);

                    if (distance < ConfigData.CloseEnoughCoordinateVariance * 2)
                    {
                        striker.BombsReady = true;
                        striker.SetIndicatorColor();
                    }
                    else
                    {
                        //Debugger.Log($"{striker.Id} is still {distance} away from {targetPoint}");
                        ship.MoveToPoint(targetPoint);
                    }
                }
            }
        }
    }
}