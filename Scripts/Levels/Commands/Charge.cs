using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Charge : Command
    {
        public HashSet<Ship> ChargingShips = new HashSet<Ship>();
        public bool IsCharging;

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Execute() method:
        //////////////////////////////////////////////////////////////////////////////

        // Method parameters for Execute()
        private ConfigData.ShootingStrategyTypes _execute_shootingStrategy;
        private long _execute_commandOutcomeId;
        private long _execute_shootingStrategyOutcomeId;
        private bool _execute_noEnemy;

        // List of ships retrieved from the squad.
        private List<Ship> _execute_ships;

        // Loop counter for iterating over _execute_ships.
        private int _execute_loopIndex;

        // Current ship being processed in the loop.
        private Ship _execute_currentShip;

        // When the current ship is a Barge.
        private Barge _execute_barge;

        /// <summary>
        ///  Sends the squad towards the enemy and follows them, when the ship is close enough, it pauses to build up "steam" and then charges forward, ramming the ship(s) in front
        ///  and damaging them. The ship takes damage from the charge even if it doesn't hit another ship. Currently only works for squads of barges
        /// </summary>
        /// <param name="strategy"></param>
        /// <param name="shootingStrategy"></param>
        /// <param name="commandOutcomeId"></param>
        /// <param name="noEnemy"></param>
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            // Store method parameters in class-level variables.
            _execute_shootingStrategy = shootingStrategy;
            _execute_commandOutcomeId = commandOutcomeId;
            _execute_shootingStrategyOutcomeId = shootingStrategyOutcomeId;
            _execute_noEnemy = noEnemy;

            base.Execute(ConfigData.CommandTypes.Charge, _execute_shootingStrategy, _execute_commandOutcomeId, _execute_shootingStrategyOutcomeId, _execute_noEnemy);
            //Debug.Log("Executing bombing run");

            IsAttacking = true;

            // loop through all the ships in the bombing squad
            Squad.Status = $"Starting charging run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();

            _execute_ships = Squad.GetShips();

            // Use a for loop with a class-level loop counter.
            for (_execute_loopIndex = 0; _execute_loopIndex < _execute_ships.Count; _execute_loopIndex++)
            {
                _execute_currentShip = _execute_ships[_execute_loopIndex];

                // loop through all the ships in the target squad
                if (_execute_currentShip.ShipType == ConfigData.ShipTypes.Barge)
                {
                    _execute_barge = (Barge)_execute_currentShip;
                    _execute_barge.HasCompletedRun = false;
                    _execute_barge.ShipsHit.Clear();
                }
                GetTargetShip(_execute_currentShip);
            }

            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            if (IsHiveMindCommand)
            {
                Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            ChargingShips.Clear();
            IsCharging = false;
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for GetTargetShip() method:
        //////////////////////////////////////////////////////////////////////////////

        // The Bomb instance retrieved from the charging ship's weapons.
        private Bomb _getTargetShip_bomb;

        // The list of ships sorted for targeting.
        private List<Ship> _getTargetShip_targetingList;

        private void GetTargetShip(Ship chargingShip)
        {
            _getTargetShip_bomb = (Bomb)chargingShip.Weapons.First();
            _getTargetShip_targetingList = _getTargetShip_bomb.MakeSortedTargetingList(true);
            if (_getTargetShip_targetingList.Count > 0)
            {
                if (!_getTargetShip_bomb.DetermineTargetShip(_getTargetShip_targetingList, true))
                {
                    _getTargetShip_targetingList = _getTargetShip_bomb.MakeSortedTargetingList(true);
                    // Couldn't find a valid target ship, potentially because too much damage has been sent to each ship already
                    if (_getTargetShip_targetingList.Count > 0)
                    {
                        _getTargetShip_bomb.SetRandomTarget(_getTargetShip_targetingList);
                    }
                    //else
                    //{
                    //    SetFinalize("No more enemy ships to target");
                    //}
                }
                //int loops = 0;
                //while (!_getTargetShip_bomb.DetermineTargetShip(_getTargetShip_bomb.MakeSortedTargetingList(true), true) && loops < 10)
                //{
                //    Squad.DamageSentToEnemyShipsBySquad.Clear();
                //    loops++;
                //}
                chargingShip.TargetEnemyShipToFollow = _getTargetShip_bomb.TargetShip;
                //if (loops == 10)
                //{
                //    Debug.Log($"Looped 10 times while trying to determine a target ship for {_getTargetShip_bomb.Name}");
                //}
            }
            else
            {
                SetFinalize("No more enemy ships to target");
            }
        }
        private void SendShipToTarget(Ship ship)
        {
            ship.MoveToPoint(ship.SetAndGetTargetEnemy().GetPosition()); // Move to the primary target ship
        }
        private bool HaveAnyShipsFinished(List<Barge> ships)
        {
            return ships.Any((ship) =>
            {
                return ship.HasCompletedRun;
            });
        }
        private bool ShouldShipPursueTarget(Barge ship)
        {
            return !ship.HasStartedCharging && !ship.HasCompletedRun && ship.HasTargetEnemyShipToFollow;
        }

        private bool HasTargetsWithinChargingRange(Barge barge)
        {
            return barge.Charge.HasTargetShip && Utilities.IsRotatedTowards(barge.gameObject, barge.GetDegreesTowardsPoint(barge.Charge.TargetShip.GetPosition())) &&
            !Utilities.HasObstaclesInTheWay(barge.GetPosition(), barge.Charge.TargetShip.GetPosition());

            //return ship.HasWeaponsTargetShips && ship.WeaponsTargetShips.Any((targetShip) => targetShip != null &&  ship.ShipsWithinRange.Contains(targetShip) 
            //&& Utilities.IsRotatedTowards(ship.gameObject, ship.GetDegreesTowardsPoint(targetShip.GetPosition()))) && !ship.ShipsWithinRange.Any((targetship) => 
            //Utilities.HasObstaclesInTheWay(ship.GetPosition(), targetship.GetPosition()));
        }




        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Timer() method:
        //////////////////////////////////////////////////////////////////////////////

        // List of all barges retrieved from the squad's ships.
        private List<Barge> _timer_barges;

        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (!EnemySquad.IsDead)
                {
                    //Debug.Log("Bombing timer");
                    Squad.Status = $"In the middle of charging run against {EnemySquad.Name}";

                    _timer_barges = Squad.GetShips().Select((ship) => (Barge)ship).ToList();
                    _timer_barges.ForEach((barge) =>
                    {
                        if (ShouldShipPursueTarget(barge))
                        {
                            if (HasTargetsWithinChargingRange(barge))
                            {
                                if (!ChargingShips.Contains(barge))
                                {
                                    ChargingShips.Add(barge);
                                    IsCharging = true;
                                    StartCoroutine(barge.ChargeForward(barge.Charge.TargetShip));
                                }
                            }
                            else
                            {
                                SendShipToTarget(barge);
                            }
                        }
                        else if (!barge.Charge.HasTargetShip)
                        {
                            GetTargetShip(barge);
                        }
                        //else // if you don't have target ships or all of them are dead
                        //{
                        //    Debug.Log($"{ship.Name} should not pursue targets because either it is charging ({ship.IsCharging}), or does not have target ships that aren't null {(ship.HasTargetShips && ship.TargetShips.Any((targetShip) => targetShip != null))}");
                        //    if (!ship.IsCharging)
                        //    {
                        //        ship.HasCompletedRun = true;
                        //    }
                        //}
                    });

                    if (HaveAnyShipsFinished(_timer_barges))
                    {
                        //Debug.Log("Ended charging run");
                        SetFinalize("Completed charging run");
                    }
                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
        }
    }
}