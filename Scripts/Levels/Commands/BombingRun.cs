using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using System.Linq;

namespace Assets.Scripts.Levels.Commands
{
    public class BombingRun : Command
    {
        public HashSet<Ship> ShipsCompletedCommand = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);

        private List<Ship> _execute_ships;
        private Ship _execute_currentShip;
        private Striker _execute_currentStriker;
        private YellowJacket _execute_currentYellowJacket;
        private int _execute_loopIndex;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy,
                            long commandOutcomeId,
                            long shootingStrategyOutcomeId)
        {
            if (CheckIfStrikersAreDefenseless())
            {
                return;
            }

            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (IsDead)
            {
                return;
            }

            IsAttacking = true;
            GetSquad().Status = $"Starting bombing run against {EnemySquad.Name}";
            PrepareDamageToSendEntries();
            _execute_ships = GetSquad().GetShips();

            for (_execute_loopIndex = 0; _execute_loopIndex < _execute_ships.Count; _execute_loopIndex++)
            {
                _execute_currentShip = _execute_ships[_execute_loopIndex];
                if (_execute_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _execute_currentStriker = (Striker)_execute_currentShip;
                    _execute_currentStriker.HasCompletedRun = false;
                    _execute_currentStriker.HasDroppedBomb = false;
                    _execute_currentStriker.HasReturnedToCarrier = false;
                }
                else if (_execute_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _execute_currentYellowJacket = (YellowJacket)_execute_currentShip;
                    _execute_currentYellowJacket.HasCompletedRun = false;
                }

                if (!GetTarget(_execute_currentShip))
                {
                    return;
                }
            }

            CommandFrequency = 2;
            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
            Level.AddTimer(CommandTimer);
            if (IsHiveMindCommand)
            {
                TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                Level.AddTimer(TimeoutTimer);
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            ShipsCompletedCommand.Clear();
            _timer_FireBargesToDetonate.Clear();
            _timerLoops = 0;
        }

        public override void SetFinalize(string cause)
        {
            if (!IsDead && GetSquad() != null)
            {
                foreach (Ship ship in GetSquad().GetShips())
                {
                    Bomb bomb = ship.Weapons.OfType<Bomb>().FirstOrDefault();
                    bomb?.ReleaseTargetReservation();
                }
            }
            base.SetFinalize(cause);
        }

        private Bomb _getTarget_bomb;
        private List<Ship> _getTarget_targetingList;
        private Striker _checkIfStrikersAreDefenseless_striker;
        private Striker _shouldShipPursueTarget_striker;

        private bool GetTarget(Ship bomber)
        {
            _getTarget_bomb = (Bomb)bomber.Weapons.First();
            _getTarget_bomb.HasCachedChanged = true;
            _getTarget_targetingList = _getTarget_bomb.MakeSortedTargetingList(true);
            if (_getTarget_targetingList.Count > 0)
            {
                if (!_getTarget_bomb.DetermineTargetShip(_getTarget_targetingList, true))
                {
                    _getTarget_targetingList = _getTarget_bomb.MakeSortedTargetingList(true);
                    if (_getTarget_targetingList.Count > 0)
                    {
                        _getTarget_bomb.SetRandomTarget(_getTarget_targetingList);
                    }
                }
                bomber.TargetEnemyShipToFollow = _getTarget_bomb.TargetShip;
            }
            else
            {
                SetFinalize("No more enemy ships to target");
                return false;
            }
            return true;
        }

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
                    SetFinalize("Strikers are defenseless, cancelling bombing run");
                    return true;
                }
            }
            return false;
        }

        private bool ShouldShipPursueTarget(Ship ship)
        {
            if (ship.HasTargetEnemyShipToFollow)
            {
                if (ship.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _shouldShipPursueTarget_striker = (Striker)ship;
                    return _shouldShipPursueTarget_striker.IsBombReady;
                }
                return true;
            }
            return false;
        }

        public void SendShipToTarget(Ship ship)
        {
            ship.MoveToPoint(ship.TargetEnemyShipToFollow.GetPosition());
        }

        private bool AreBombersCloseToEnemyTargets()
        {
            return GetSquad().GetShips().All((ship) =>
            {
                return ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow) ||
                       (ship.ShipType == ConfigData.ShipTypes.Striker && ((Striker)ship).HasCompletedRun);
            }) && GetSquad().GetShips().Any((ship) => ship.ProximityCollider.NearbyEnemyShips.Contains(ship.TargetEnemyShipToFollow));
        }

        private Ship _haveAllShipsFinished_currentShip;
        private Striker _finishingStriker;
        private YellowJacket _haveAllShipsFinished_yellowJacket;
        private Ship _haveAllShipsBombed_currentShip;
        private Striker _haveAllShipsBombed_striker;
        private YellowJacket _haveAllShipsBombed_yellowJacket;

        private bool HaveAllShipsFinished(List<Ship> ships)
        {
            return ships.All((ship) =>
            {
                _haveAllShipsFinished_currentShip = ship;
                if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _finishingStriker = (Striker)_haveAllShipsFinished_currentShip;
                    return _finishingStriker.HasCompletedRun &&
                           (_finishingStriker.HasReturnedToCarrier || _finishingStriker.Carrier == null || _finishingStriker.Carrier.IsDead);
                }
                if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _haveAllShipsFinished_yellowJacket = (YellowJacket)_haveAllShipsFinished_currentShip;
                    return _haveAllShipsFinished_yellowJacket.HasCompletedRun;
                }
                if (_haveAllShipsFinished_currentShip.ShipType == ConfigData.ShipTypes.FireBarge)
                {
                    return false;
                }
                return true;
            });
        }

        private bool HaveAllShipsBombed(List<Ship> ships)
        {
            return ships.All((ship) =>
            {
                _haveAllShipsBombed_currentShip = ship;
                if (_haveAllShipsBombed_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                {
                    _haveAllShipsBombed_striker = (Striker)_haveAllShipsBombed_currentShip;
                    return _haveAllShipsBombed_striker.HasCompletedRun;
                }
                if (_haveAllShipsBombed_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                {
                    _haveAllShipsBombed_yellowJacket = (YellowJacket)_haveAllShipsBombed_currentShip;
                    return _haveAllShipsBombed_yellowJacket.HasCompletedRun;
                }
                return true;
            });
        }

        private int _timerLoops;
        private List<Ship> _timer_ships;
        private List<long> _timer_FireBargesToDetonate = new List<long>();
        private Ship _timer_currentShip;
        private Striker _timer_striker;
        private YellowJacket _timer_yellowJacket;
        private int _timer_firebargeLoopIndex;
        private int _timer_loopIndex;
        private Ship _timer_fireBargeCandidate;
        private List<Ship> _endBombingRun_ships;
        private Ship _endBombingRun_currentShip;
        private Striker _endBombingRun_striker;
        private int _endBombingRun_loopIndex;

        private void Timer()
        {
            if (IsDead)
            {
                return;
            }

            if (!EnemySquad.IsDead)
            {
                _timerLoops++;
                GetSquad().Status = $"In the middle of bombing run against {EnemySquad.Name}";
                _timer_ships = GetSquad().GetShips();
                _timer_FireBargesToDetonate.Clear();

                for (_timer_loopIndex = 0; _timer_loopIndex < _timer_ships.Count; _timer_loopIndex++)
                {
                    _timer_currentShip = _timer_ships[_timer_loopIndex];
                    if (ShipsCompletedCommand.Contains(_timer_currentShip))
                    {
                        continue;
                    }

                    if (ShouldShipPursueTarget(_timer_currentShip))
                    {
                        SendShipToTarget(_timer_currentShip);
                        if (GetSquad().IsHiveMindControlled &&
                            _timer_currentShip.ShipType == ConfigData.ShipTypes.FireBarge &&
                            _timer_currentShip.ProximityCollider.NearbyEnemyShips.Contains(_timer_currentShip.TargetEnemyShipToFollow))
                        {
                            _timer_FireBargesToDetonate.Add(_timer_currentShip.Id);
                        }
                    }
                    else
                    {
                        if (_timer_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                        {
                            _timer_striker = (Striker)_timer_currentShip;
                            if (EnemySquad.IsDead)
                            {
                                _timer_striker.CompleteRun();
                            }
                            else if (_timer_striker.IsBombReady && !GetTarget(_timer_currentShip))
                            {
                                return;
                            }
                        }
                        else if (_timer_currentShip.ShipType == ConfigData.ShipTypes.YellowJacket)
                        {
                            _timer_yellowJacket = (YellowJacket)_timer_currentShip;
                            if (EnemySquad.IsDead)
                            {
                                _timer_yellowJacket.HasCompletedRun = true;
                            }
                            else if (!GetTarget(_timer_currentShip))
                            {
                                return;
                            }
                        }
                    }

                    if (_timer_currentShip.ShipType == ConfigData.ShipTypes.Striker)
                    {
                        ((Striker)_timer_currentShip).ReturnToCarrierIfNecessary();
                    }
                }

                for (_timer_firebargeLoopIndex = 0; _timer_firebargeLoopIndex < _timer_FireBargesToDetonate.Count; _timer_firebargeLoopIndex++)
                {
                    _timer_fireBargeCandidate = Level.State.GetShipById(_timer_FireBargesToDetonate[_timer_firebargeLoopIndex]);
                    if (_timer_fireBargeCandidate is FireBarge fireBarge && !fireBarge.IsDead)
                    {
                        fireBarge.Detonate();
                    }
                    if (IsDead)
                    {
                        return;
                    }
                }

                if (HaveAllShipsFinished(_timer_ships))
                {
                    EndBombingRun();
                }
                else if (!IsCloseToTarget && !EnemySquad.IsDead)
                {
                    if (AreBombersCloseToEnemyTargets())
                    {
                        Level.CancelTimer(CommandTimer);
                        CommandFrequency = .25f;
                        IsCloseToTarget = true;
                        CommandTimer.Reuse(CommandFrequency, Timer, true);
                        Level.AddTimer(CommandTimer);
                    }
                }
                else if (IsCloseToTarget && _timerLoops % 4 == 0 &&
                         (HaveAllShipsBombed(_timer_ships) || EnemySquad.IsDead || !AreBombersCloseToEnemyTargets()))
                {
                    Level.CancelTimer(CommandTimer);
                    CommandFrequency = 2f;
                    IsCloseToTarget = false;
                    CommandTimer.Reuse(CommandFrequency, Timer, true);
                    Level.AddTimer(CommandTimer);
                }
            }
            else
            {
                EndBombingRun();
            }
        }

        private void EndBombingRun()
        {
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

                // A dead target ends the attack phase, but Strikers still need the active
                // command timer to fly back and reload. Finalizing here stranded them after
                // a single return-to-carrier movement update.
                if (!HaveAllShipsFinished(_endBombingRun_ships))
                {
                    return;
                }
            }

            if (!IsDead)
            {
                SetFinalize("Completed bombing run");
            }
        }
    }
}