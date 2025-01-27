using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class BombingRun : Command
    {
        public HashSet<Ship> ShipsCompletedCommand = new HashSet<Ship>();
        /// <summary>
        /// Only available to Yellow Jackets, FireBarges, and Strikers. Sends all ships straight onto the ships of the squad and back to the carrier if applicable
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            //Debug.Log("Executing bombing run");


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
                if (ship.ShipType == ConfigData.ShipTypes.Striker)
                {
                    Striker striker = (Striker)ship;
                    striker.HasCompletedRun = false;
                    striker.HasDroppedBomb = false;
                    striker.HasReturnedToCarrier = false;
                }
                else if (ship.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    YellowJacket yellowJacket = (YellowJacket)ship;
                    yellowJacket.HasCompletedRun = false;
                }

                GetTarget(ship);
            }
            CommandFrequency = 2;
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            if (IsHiveMindCommand)
            {
                Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }

        }
        /// <summary>
        /// Finds a target ship for the ship's bomb and then makes that ship the enemy ship to follow as well
        /// </summary>
        /// <param name="bomber"></param>
        private void GetTarget(Ship bomber)
        {
            Bomb bomb = (Bomb)bomber.Weapons.First();
            //int loops = 0;
            bomb.HasCachedChanged = true;
            List<Ship> targetingList = bomb.MakeSortedTargetingList(true);
            if (targetingList.Count > 0)
            {
                if (!bomb.DetermineTargetShip(targetingList, true))
                {
                    targetingList = bomb.MakeSortedTargetingList(true);
                    // Couldn't find a valid target ship, potentially because too much damage has been sent to each ship already
                    if (targetingList.Count > 0)
                    {
                        bomb.SetRandomTarget(targetingList);

                    }
                    //else
                    //{
                    //    SetFinalize("No more enemy ships to target");
                    //}
                }
                //while (!bomb.DetermineTargetShip(bomb.MakeSortedTargetingList(true), true) && loops < 10)
                //{
                //    Squad.DamageSentToEnemyShipsBySquad.Clear();
                //    loops++;
                //}
                bomber.TargetEnemyShipToFollow = bomb.TargetShip;
                //if (loops == 10)
                //{
                //    Debug.Log($"Looped 10 times while trying to determine a target ship for {bomb.Name}");
                //}
            }
            else
            {
                SetFinalize("No more enemy ships to target");
            }

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
                    Squad.BannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                    Squad.BannedStrats.Add(ConfigData.CommandTypes.Circle);
                    Squad.BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                    Squad.BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                    Squad.BannedStrats.Add(ConfigData.CommandTypes.InAndOut);

                    //Debug.Log("Strikers are defenseless, cancelling bombing run");
                    SetFinalize("Strikers are defenseless, cancelling bombing run");
                    return true;
                }
            }
            return false;
        }
        /// <summary>
        /// Does the ship have a target to follow, and if it is a striker does it have its bomb ready? 
        /// </summary>
        /// <param name="ship"></param>
        /// <returns></returns>
        private bool ShouldShipPursueTarget(Ship ship)
        {
            if (ship.HasTargetEnemyShipToFollow) // if the ship has target ships and they're not all dead
            {
                if (ship.ShipType == ConfigData.ShipTypes.Striker)
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
        /// <summary>
        /// Move the ship towards its target enemy ship
        /// </summary>
        /// <param name="ship"></param>
        public void SendShipToTarget(Ship ship)
        {
            ship.MoveToPoint(ship.TargetEnemyShipToFollow.GetPosition()); // Move to the primary target ship
        }
        /// <summary>
        /// Checks to see if all ships are either near their target enemy ships OR that at least one is and the others have already completed their run
        /// </summary>
        /// <returns></returns>
        private bool AreBombersCloseToEnemyTargets()
        {
            return Squad.GetShips().All((ship) =>
            {
                return ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow) || (ship.ShipType == ConfigData.ShipTypes.Striker && ((Striker)ship).HasCompletedRun);
            }) && Squad.GetShips().Any((ship) => ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow));
        }
        /// <summary>
        /// Have all the ships dropped their bombs if they have them and then returned to their carrier if necessary
        /// </summary>
        /// <param name="ships"></param>
        /// <returns></returns>
        private bool HaveAllShipsFinished(List<Ship> ships)
        {
            return ships.All((ship) => // if all of the ships have completed their run and are either yellow jackets or are strikers who have reloaded or have no carrier
            {
                if (ship.ShipType == ConfigData.ShipTypes.Striker)
                {
                    Striker striker = (Striker)ship;
                    return striker.HasCompletedRun && (striker.HasReturnedToCarrier || !striker.HasCarrier);
                }
                else if (ship.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    YellowJacket yellowJacket = (YellowJacket)ship;
                    return yellowJacket.HasCompletedRun;
                }
                else if (ship.ShipType == ConfigData.ShipTypes.FireBarge)
                {
                    return false;
                }
                return true;
            });
        }
        private bool HaveAllShipsBombed(List<Ship> ships)
        {
            return ships.All((ship) => // if all of the ships have completed their run and are either yellow jackets or are strikers who have reloaded or have no carrier
            {
                if (ship.ShipType == ConfigData.ShipTypes.Striker)
                {
                    Striker striker = (Striker)ship;
                    return striker.HasCompletedRun;
                }
                else if (ship.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    YellowJacket yellowJacket = (YellowJacket)ship;
                    return yellowJacket.HasCompletedRun;
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
                List<long> FireBargesToDetonate = new List<long>();
                ships.ForEach((ship) =>
                {
                    if (!ShipsCompletedCommand.Contains(ship))
                    {
                        if (ShouldShipPursueTarget(ship))
                        {

                            SendShipToTarget(ship);
                            if (Squad.IsHiveMindControlled && ship.ShipType == ConfigData.ShipTypes.FireBarge && ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow))
                            {
                                // if you're a Fire Barge and within detonation distance of your target, detonate
                                Debug.Log($"{ship.Name} is hivemind controlled and on a bombing run and near its target enemy and so it's going to detonate. ");
                                FireBargesToDetonate.Add(ship.Id);
                            }
                        }
                        else
                        {

                            //Debug.Log($"{ship.Name} should not pursure its target ({ship.TargetEnemyShipToFollow}) and so it's going back to its carrier");
                            if (ship.ShipType == ConfigData.ShipTypes.Striker)
                            {
                                Striker striker = (Striker)ship;
                                if (EnemySquad.IsDead)
                                {
                                    striker.CompleteRun();
                                }
                                else if (striker.IsBombReady)
                                {
                                    GetTarget(ship);
                                }
                            }
                            else if (ship.ShipType == ConfigData.ShipTypes.YellowJacket)
                            {
                                YellowJacket yellowJacket = (YellowJacket)ship;
                                if (EnemySquad.IsDead)
                                {
                                    yellowJacket.HasCompletedRun = true;
                                }
                                else
                                {
                                    GetTarget(ship);
                                }
                            }
                        }
                        if (ship.ShipType == ConfigData.ShipTypes.Striker)
                        {
                            Striker striker = (Striker)ship;
                            striker.ReturnToCarrierIfNecessary();
                        }
                    }
                    
                });

                // This is necessary to prevent modifying the list when the Fire Barge(s) is killed
                for (int i = 0; i < FireBargesToDetonate.Count; i++)
                {
                    ((FireBarge)Level.State.GetShipById(FireBargesToDetonate[i])).Detonate();
                }


                if (HaveAllShipsFinished(ships))
                {
                    EndBombingRun();
                }

                if (!IsCloseToTarget && !EnemySquad.IsDead)
                {
                    if (AreBombersCloseToEnemyTargets())
                    {
                        //Debug.Log($"{Squad.Name} is on a bombing run and close to {EnemySquad.Name}");
                        CancelInvoke(nameof(Timer));
                        CommandFrequency = .25f;
                        IsCloseToTarget = true;
                        InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                    }

                }
                else if (IsCloseToTarget &&  _timerLoops % 4 == 0 && (HaveAllShipsBombed(ships) || (EnemySquad.IsDead || !AreBombersCloseToEnemyTargets())))
                {
                    //Debug.Log($"{Squad.Name} is on a bombing run and no longer close to {EnemySquad.Name}");
                    CancelInvoke(nameof(Timer));
                    CommandFrequency = 2f;
                    IsCloseToTarget = false;
                    InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                }
            }
            
        }

        private void EndBombingRun()
        {
            //Debug.Log("Ended bombing run");
            CancelInvoke(nameof(Timer));
            SetFinalize("Completed bombing run");

            if (Squad.HasOnlyStrikers)
            {
                foreach (Ship ship in Squad.GetShips())
                {

                    Striker striker = (Striker)ship;
                    striker.HasCompletedRun = true;
                    striker.ReturnToCarrierIfNecessary();
                }
            }

        }
    }
}