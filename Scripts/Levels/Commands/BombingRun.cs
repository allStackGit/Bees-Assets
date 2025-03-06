using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;

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
        // Class-level variables for the Execute method:

        // Class-level variables for the Execute() method:

        // Used in Execute(): holds the list of ships from the squad.
        private List<Ship> _execute_ships;

        // Used in Execute() loop: current ship being processed.
        private Ship _execute_currentShip;

        // Used in Execute() loop when a ship is a Striker.
        private Striker _execute_currentStriker;

        // Used in Execute() loop when a ship is a YellowJacket.
        private YellowJacket _execute_currentYellowJacket;

        // Used in Execute() loop: loop index.
        private int _execute_loopIndex;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy,
                            long commandOutcomeId,
                            long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            // Debug.Log("Executing bombing run");

            if (CheckIfStrikersAreDefenseless())
            {
                return;
            }

            // Setup status and damage
            IsAttacking = true;
            GetSquad().Status = $"Starting bombing run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();

            // Retrieve and store the ships from the squad.
            _execute_ships = GetSquad().GetShips();

            // Iterate through all the ships using a for loop that utilizes the class-level loop index.
            for (_execute_loopIndex = 0; _execute_loopIndex < _execute_ships.Count; _execute_loopIndex++)
            {
                _execute_currentShip = _execute_ships[_execute_loopIndex];

                // If the ship is a Striker, cast it and reset its run-related flags.
                if (_execute_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _execute_currentStriker = (Striker)_execute_currentShip;
                    _execute_currentStriker.HasCompletedRun = false;
                    _execute_currentStriker.HasDroppedBomb = false;
                    _execute_currentStriker.HasReturnedToCarrier = false;
                }
                // If the ship is a YellowJacket, cast it and reset its run flag.
                else if (_execute_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _execute_currentYellowJacket = (YellowJacket)_execute_currentShip;
                    _execute_currentYellowJacket.HasCompletedRun = false;
                }

                // Process the current ship to get its target.
                if (!GetTarget(_execute_currentShip))
                {
                    return; // There was no target and had to finalize the command
                }
            }

            Timer();
            CommandFrequency = 2;
            CommandTimer.Reuse(CommandFrequency, Timer, true);
            Level.AddTimer(CommandTimer);
            //InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                Level.AddTimer(TimeoutTimer);
                //Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            ShipsCompletedCommand.Clear();
        }

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for GetTarget() method:
        //////////////////////////////////////////////////////////////////////////////
        private Bomb _getTarget_bomb;
        private List<Ship> _getTarget_targetingList;

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variable for CheckIfStrikersAreDefenseless() method:
        //////////////////////////////////////////////////////////////////////////////
        private Striker _checkIfStrikersAreDefenseless_striker;

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variable for ShouldShipPursueTarget() method:
        //////////////////////////////////////////////////////////////////////////////
        private Striker _shouldShipPursueTarget_striker;

        //////////////////////////////////////////////////////////////////////////////
        // Method: GetTarget
        //////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Finds a target ship for the ship's bomb and then makes that ship the enemy ship to follow as well
        /// </summary>
        /// <param name="bomber"></param>
        private bool GetTarget(Ship bomber)
        {
            _getTarget_bomb = (Bomb)bomber.Weapons.First();
            //int loops = 0;
            _getTarget_bomb.HasCachedChanged = true;
            _getTarget_targetingList = _getTarget_bomb.MakeSortedTargetingList(true);
            if (_getTarget_targetingList.Count > 0)
            {
                if (!_getTarget_bomb.DetermineTargetShip(_getTarget_targetingList, true))
                {
                    _getTarget_targetingList = _getTarget_bomb.MakeSortedTargetingList(true);
                    // Couldn't find a valid target ship, potentially because too much damage has been sent to each ship already
                    if (_getTarget_targetingList.Count > 0)
                    {
                        _getTarget_bomb.SetRandomTarget(_getTarget_targetingList);

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
                bomber.TargetEnemyShipToFollow = _getTarget_bomb.TargetShip;
                //if (loops == 10)
                //{
                //    Debug.Log($"Looped 10 times while trying to determine a target ship for {bomb.Name}");
                //}
            }
            else
            {
                SetFinalize("No more enemy ships to target");
                return false;

            }
            return true;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Method: CheckIfStrikersAreDefenseless
        //////////////////////////////////////////////////////////////////////////////
        private bool CheckIfStrikersAreDefenseless()
        {
            if (GetSquad().IsCarrierSquad)
            {
                if (GetSquad().GetShips().All((s) =>
                {
                    _checkIfStrikersAreDefenseless_striker = (Striker)s;
                    return !_checkIfStrikersAreDefenseless_striker.IsBombReady && _checkIfStrikersAreDefenseless_striker.Carrier == null;
                }))
                {
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                    GetSquad().BannedStrats.Add(ConfigData.CommandTypes.InAndOut);

                    //Debug.Log("Strikers are defenseless, cancelling bombing run");
                    SetFinalize("Strikers are defenseless, cancelling bombing run");
                    return true;
                }
            }
            return false;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Method: ShouldShipPursueTarget
        //////////////////////////////////////////////////////////////////////////////
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
                    _shouldShipPursueTarget_striker = (Striker)ship;
                    return _shouldShipPursueTarget_striker.IsBombReady; // if it's a striker and its bombs are ready 
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
            return GetSquad().GetShips().All((ship) =>
            {
                return ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow) || (ship.ShipType == ConfigData.ShipTypes.Striker && ((Striker)ship).HasCompletedRun);
            }) && GetSquad().GetShips().Any((ship) => ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow));
            //try
            //{
            //    return GetSquad().GetShips().All((ship) =>
            //    {
            //        return ship.Vision.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow) || (ship.ShipType == ConfigData.ShipTypes.Striker && ((Striker)ship).HasCompletedRun);
            //    }) && GetSquad().GetShips().Any((ship) => ship.Vision.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow));
            //}
            //catch(Exception e)
            //{
            //    Debug.Log($"Ships: {Utilities.ListToString(GetSquad().GetShips())}, Are any ships null? {GetSquad().GetShips().Any((s) => s == null)}, Are any ships vision colliders null?" +
            //        $" {GetSquad().GetShips().Any((s) => s?.Vision == null)}," +
            //        $"Are any ships nearby enemy ships null? {GetSquad().GetShips().Any((s) => s?.Vision?.NearbyEnemyShips == null)}");
            //    throw e;
            //}

        }
        /// <summary>
        /// Have all the ships dropped their bombs if they have them and then returned to their carrier if necessary
        /// </summary>
        /// <param name="ships"></param>
        /// <returns></returns>
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HaveAllShipsFinished() method:
        //////////////////////////////////////////////////////////////////////////////
        private Ship _haveAllShipsFinished_currentShip;
        private Striker _finishingStriker;
        private YellowJacket _haveAllShipsFinished_yellowJacket;

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HaveAllShipsBombed() method:
        //////////////////////////////////////////////////////////////////////////////
        private Ship _haveAllShipsBombed_currentShip;
        private Striker _haveAllShipsBombed_striker;
        private YellowJacket _haveAllShipsBombed_yellowJacket;
        /// <summary>
        /// Have all the ships dropped their bombs if they have them and then returned to their carrier if necessary
        /// </summary>
        /// <param name="ships"></param>
        /// <returns></returns>
        private bool HaveAllShipsFinished(List<Ship> ships)
        {
            return ships.All((ship) => // if all of the ships have completed their run and are either yellow jackets or are strikers who have reloaded or have no carrier
            {
                _haveAllShipsFinished_currentShip = ship;
                if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _finishingStriker = (Striker)_haveAllShipsFinished_currentShip;
                    return _finishingStriker.HasCompletedRun &&
                           (_finishingStriker.HasReturnedToCarrier || _finishingStriker.Carrier.IsDead);
                }
                else if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _haveAllShipsFinished_yellowJacket = (YellowJacket)_haveAllShipsFinished_currentShip;
                    return _haveAllShipsFinished_yellowJacket.HasCompletedRun;
                }
                else if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.FireBarge)
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
                _haveAllShipsBombed_currentShip = ship;
                if (_haveAllShipsBombed_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _haveAllShipsBombed_striker = (Striker)_haveAllShipsBombed_currentShip;
                    return _haveAllShipsBombed_striker.HasCompletedRun;
                }
                else if (_haveAllShipsBombed_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _haveAllShipsBombed_yellowJacket = (YellowJacket)_haveAllShipsBombed_currentShip;
                    return _haveAllShipsBombed_yellowJacket.HasCompletedRun;
                }
                return true;
            });
        }

        private int _timerLoops;
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Timer() method:
        //////////////////////////////////////////////////////////////////////////////

        // Used in Timer(): list of ships retrieved from the squad.
        private List<Ship> _timer_ships;

        // Used in Timer(): list of FireBarge IDs that need to detonate.
        private List<long> _timer_FireBargesToDetonate = new List<long>();

        // Used in Timer() loop: current ship being processed.
        private Ship _timer_currentShip;

        // Used in Timer() loop when a ship is a Striker.
        private Striker _timer_striker;

        // Used in Timer() loop when a ship is a YellowJacket.
        private YellowJacket _timer_yellowJacket;

        // Used in Timer(): loop counter for processing FireBargesToDetonate.
        private int _timer_firebargeLoopIndex;
        private int _timer_loopIndex;


        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for EndBombingRun() method:
        //////////////////////////////////////////////////////////////////////////////

        // Used in EndBombingRun(): list of ships retrieved from the squad.
        private List<Ship> _endBombingRun_ships;

        // Used in EndBombingRun() loop: current ship being processed.
        private Ship _endBombingRun_currentShip;

        // Used in EndBombingRun() loop when a ship is a Striker.
        private Striker _endBombingRun_striker;

        // Used in EndBombingRun(): loop counter for processing ships.
        private int _endBombingRun_loopIndex;



        private void Timer()
        {
            if (!IsDead)
            {
                if (!EnemySquad.IsDead)
                {
                    _timerLoops++; // Assuming _timerLoops is already declared as a class-level variable
                                   //Debug.Log("Bombing timer");
                    GetSquad().Status = $"In the middle of bombing run against {EnemySquad.Name}";
                    _timer_ships = GetSquad().GetShips();
                    _timer_FireBargesToDetonate.Clear();

                    // Process each ship in the squad.
                    for (_timer_loopIndex = 0; _timer_loopIndex < _timer_ships.Count; _timer_loopIndex++)
                    {
                        _timer_currentShip = _timer_ships[_timer_loopIndex];
                        if (!ShipsCompletedCommand.Contains(_timer_currentShip))
                        {
                            if (ShouldShipPursueTarget(_timer_currentShip))
                            {
                                SendShipToTarget(_timer_currentShip);
                                if (GetSquad().IsHiveMindControlled &&
                                    _timer_currentShip.ShipType == ConfigData.ShipTypes.FireBarge &&
                                    _timer_currentShip.ProximityCollider.NearbyEnemyShips.Contains(_timer_currentShip.TargetEnemyShipToFollow))
                                {
                                    // if you're a Fire Barge and within detonation distance of your target, detonate
                                    //Debug.Log($"{_timer_currentShip.Name} is hivemind controlled and on a bombing run and near its target enemy and so it's going to detonate. ");
                                    _timer_FireBargesToDetonate.Add(_timer_currentShip.Id);
                                }
                            }
                            else
                            {
                                //Debug.Log($"{_timer_currentShip.Name} should not pursure its target ({_timer_currentShip.TargetEnemyShipToFollow}) and so it's going back to its carrier");
                                if (_timer_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                                {
                                    _timer_striker = (Striker)_timer_currentShip;
                                    if (EnemySquad.IsDead)
                                    {
                                        _timer_striker.CompleteRun();
                                    }
                                    else if (_timer_striker.IsBombReady)
                                    {
                                        if (!GetTarget(_timer_currentShip))
                                        {
                                            return; // There was no target and had to finalize the command
                                        }
                                    }
                                }
                                else if (_timer_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                                {
                                    _timer_yellowJacket = (YellowJacket)_timer_currentShip;
                                    if (EnemySquad.IsDead)
                                    {
                                        _timer_yellowJacket.HasCompletedRun = true;
                                    }
                                    else
                                    {
                                        if (!GetTarget(_timer_currentShip))
                                        {
                                            return; // There was no target and had to finalize the command
                                        }
                                    }
                                }
                            }
                            if (_timer_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                            {
                                _timer_striker = (Striker)_timer_currentShip;
                                _timer_striker.ReturnToCarrierIfNecessary();
                            }
                        }
                    }

                    // This is necessary to prevent modifying the list when the Fire Barge(s) is killed.
                    for (_timer_firebargeLoopIndex = 0; _timer_firebargeLoopIndex < _timer_FireBargesToDetonate.Count; _timer_firebargeLoopIndex++)
                    {
                        ((FireBarge)Level.State.GetShipById(_timer_FireBargesToDetonate[_timer_firebargeLoopIndex])).Detonate();
                    }

                    if (HaveAllShipsFinished(_timer_ships))
                    {
                        EndBombingRun();
                    }
                    else
                    {
                        if (!IsCloseToTarget && !EnemySquad.IsDead)
                        {
                            if (AreBombersCloseToEnemyTargets())
                            {
                                //Debug.Log($"{Squad.Name} is on a bombing run and close to {EnemySquad.Name}");
                                Level.CancelTimer(CommandTimer);
                                //CancelInvoke(nameof(Timer));
                                CommandFrequency = .25f;
                                IsCloseToTarget = true;
                                CommandTimer.Reuse(CommandFrequency, Timer, true);
                                Level.AddTimer(CommandTimer);
                                //InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                            }
                        }
                        else if (IsCloseToTarget && _timerLoops % 4 == 0 && (HaveAllShipsBombed(_timer_ships) || (EnemySquad.IsDead || !AreBombersCloseToEnemyTargets())))
                        {
                            //Debug.Log($"{Squad.Name} is on a bombing run and no longer close to {EnemySquad.Name}");
                            Level.CancelTimer(CommandTimer);
                            //CancelInvoke(nameof(Timer));
                            CommandFrequency = 2f;
                            IsCloseToTarget = false;
                            CommandTimer.Reuse(CommandFrequency, Timer, true);
                            Level.AddTimer(CommandTimer);
                            //InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
                        }
                    }
                }
                else
                {
                    EndBombingRun();
                }
                

                
            }
        }

        private void EndBombingRun()
        {
            //Debug.Log("Ended bombing run");
            //CancelInvoke(nameof(Timer));

            if (GetSquad().HasOnlyStrikers)
            {
                _endBombingRun_ships = GetSquad().GetShips();
                for (_endBombingRun_loopIndex = 0; _endBombingRun_loopIndex < _endBombingRun_ships.Count; _endBombingRun_loopIndex++)
                {
                    _endBombingRun_currentShip = _endBombingRun_ships[_endBombingRun_loopIndex];
                    _endBombingRun_striker = (Striker)_endBombingRun_currentShip;
                    _endBombingRun_striker.HasCompletedRun = true;
                    _endBombingRun_striker.ReturnToCarrierIfNecessary();
                }
            }

            if (!IsDead)
            {
                SetFinalize("Completed bombing run");
            }

        }
    }
}