using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Heal : Command
    {
        public List<Beehive> TargetBeehives = new List<Beehive>();
        public List<Ship> ShipsWaitingToHeal = new List<Ship>();
        public List<Ship> ShipsHealing = new List<Ship>();
        public bool IsHealing;
        private int _spotsAvailable;
        private int _index;
        private Beehive _beehive;
        private readonly Queue<Ship> _shipsThatNeedBeehive = new Queue<Ship>();
        private readonly List<Ship> _healingCandidates = new List<Ship>();
        private Ship _ship;
        private Dictionary<long, Beehive> _shipsAndBeehives = new Dictionary<long, Beehive>();
        private readonly HashSet<Beehive> _healingCrossBeehives = new HashSet<Beehive>();
        private readonly HashSet<Ship> _reservedShipsToRelease = new HashSet<Ship>();
        private int _healingTimerCount;

        private static int CompareHealingNeed(Ship a, Ship b)
        {
            return (a.Health - a.OriginalHealth).CompareTo(b.Health - b.OriginalHealth);
        }

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, List<Beehive> beehives)
        {
            TargetBeehives.Clear();
            for (int i = 0; i < beehives.Count; i++)
            {
                Beehive beehive = beehives[i];
                if (beehive != null && !beehive.IsDead)
                {
                    TargetBeehives.Add(beehive);
                }
            }

            if (TargetBeehives.Count > 0)
            {
                _healingCandidates.Clear();
                List<Ship> squadShips = GetSquad().GetShips();
                for (int i = 0; i < squadShips.Count; i++)
                {
                    Ship ship = squadShips[i];
                    if (!ship.IsDead && ship.Health < ship.MaxHealth)
                    {
                        _healingCandidates.Add(ship);
                    }
                }
                _healingCandidates.Sort(CompareHealingNeed);
                _shipsThatNeedBeehive.Clear();
                for (int i = 0; i < _healingCandidates.Count; i++)
                {
                    _shipsThatNeedBeehive.Enqueue(_healingCandidates[i]);
                }

                if (_shipsThatNeedBeehive.Count == 0)
                {
                    SetFinalize("There are no damaged ships to heal");
                    return;
                }

                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                AssignAvailableHealingSlots();

                if (ShipsWaitingToHeal.Count == 0)
                {
                    SetFinalize("There are no available beehive healing slots");
                    return;
                }

                GetSquad().Status = $"Moving to {TargetBeehives.Count} beehives to heal";
                MoveToBeehives();
                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);

                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                    Level.AddTimer(TimeoutTimer);
                }
            }
            else
            {
                SetFinalize("The beehives don't exist anymore, or there were no beehives around");
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            TargetBeehives.Clear();
            ShipsWaitingToHeal.Clear();
            ShipsHealing.Clear();
            _shipsAndBeehives.Clear();
            _shipsThatLostBeehiveOrDied.Clear();
            _healingCrossBeehives.Clear();
            _reservedShipsToRelease.Clear();
            _healingCandidates.Clear();
            _shipsThatNeedBeehive.Clear();
            _healingTimerCount = 0;
            IsHealing = false;
        }

        private void AssignAvailableHealingSlots()
        {
            if (_shipsThatNeedBeehive.Count == 0)
            {
                return;
            }

            int shipsToCheck = _shipsThatNeedBeehive.Count;
            for (_index = 0; _index < shipsToCheck; _index++)
            {
                _ship = _shipsThatNeedBeehive.Dequeue();
                if (_ship != null && !_ship.IsDead && _ship.Health < _ship.MaxHealth && !_shipsAndBeehives.ContainsKey(_ship.Id))
                {
                    _shipsThatNeedBeehive.Enqueue(_ship);
                }
            }

            for (_index = 0; _index < TargetBeehives.Count && _shipsThatNeedBeehive.Count > 0; _index++)
            {
                _beehive = TargetBeehives[_index];
                if (_beehive == null || _beehive.IsDead)
                {
                    continue;
                }

                _spotsAvailable = 4 - _beehive.ShipsHealingHere.Count;
                while (_spotsAvailable > 0 && _shipsThatNeedBeehive.Count > 0)
                {
                    _ship = _shipsThatNeedBeehive.Dequeue();
                    if (_ship == null || _ship.IsDead || _ship.Health >= _ship.MaxHealth || _shipsAndBeehives.ContainsKey(_ship.Id))
                    {
                        continue;
                    }

                    if (!ShipsWaitingToHeal.Contains(_ship))
                    {
                        ShipsWaitingToHeal.Add(_ship);
                    }
                    _beehive.ShipsHealingHere.Add(_ship);
                    _shipsAndBeehives[_ship.Id] = _beehive;
                    _spotsAvailable--;
                }
            }
        }

        private List<Ship> _shipsThatLostBeehiveOrDied = new List<Ship>();
        public void Timer()
        {
            if (!GetSquad().IsDead)
            {
                for (_index = TargetBeehives.Count - 1; _index >= 0; _index--)
                {
                    if (TargetBeehives[_index] == null || TargetBeehives[_index].IsDead)
                    {
                        TargetBeehives.RemoveAt(_index);
                    }
                }
                if (TargetBeehives.Count > 0)
                {
                    MoveToBeehives();
                }
                else
                {
                    SetFinalize("The beehives died");
                }
            }
        }

        private ScaledTimer _healingTimer = new ScaledTimer();
        public void StartHealingTimer()
        {
            IsHealing = true;
            _healingTimer.Reuse(1, HealShips, true);
            Level.AddTimer(_healingTimer);
        }

        public void ShipReachedBeehive(Ship ship)
        {
            if (ship == null || !_shipsAndBeehives.ContainsKey(ship.Id))
            {
                return;
            }

            ShipsWaitingToHeal.Remove(ship);
            if (!ShipsHealing.Contains(ship))
            {
                ShipsHealing.Add(ship);
            }

            if (!IsHealing)
            {
                StartHealingTimer();
            }
        }

        public bool IsShipActivelyHealing(Ship ship)
        {
            return ship != null && ShipsHealing.Contains(ship);
        }

        public void ShipBecameUnavailable(Ship ship)
        {
            if (ship == null || IsDead)
            {
                return;
            }

            ReleaseHealingReservation(ship);
            FinalizeIfAssignedShipsAreDone();
        }

        private void ReleaseHealingReservation(Ship ship, bool requeueIfStillDamaged = false)
        {
            if (ship == null)
            {
                return;
            }

            if (_shipsAndBeehives.TryGetValue(ship.Id, out Beehive reservedBeehive))
            {
                if (reservedBeehive != null)
                {
                    reservedBeehive.ShipsHealingHere.Remove(ship);
                }
                _shipsAndBeehives.Remove(ship.Id);
            }

            ShipsWaitingToHeal.Remove(ship);
            ShipsHealing.Remove(ship);

            if (requeueIfStillDamaged && !ship.IsDead && ship.Health < ship.MaxHealth &&
                !_shipsThatNeedBeehive.Contains(ship))
            {
                _shipsThatNeedBeehive.Enqueue(ship);
            }
        }

        private void FinalizeIfAssignedShipsAreDone()
        {
            AssignAvailableHealingSlots();
            if (!IsDead && ShipsWaitingToHeal.Count == 0 && ShipsHealing.Count == 0 &&
                _shipsThatNeedBeehive.Count == 0)
            {
                SetFinalize("All damaged ships finished healing or became unavailable");
            }
        }

        public void MoveToBeehives()
        {
            for (_index = 0; _index < ShipsWaitingToHeal.Count; _index++)
            {
                _ship = ShipsWaitingToHeal[_index];
                if (!_shipsAndBeehives.TryGetValue(_ship.Id, out _beehive) || _beehive == null || _beehive.IsDead || _ship.IsDead || _ship.Health >= _ship.MaxHealth)
                {
                    _shipsThatLostBeehiveOrDied.Add(_ship);
                    continue;
                }

                _ship.MoveToTrackedPoint(_beehive.GetPosition());
            }

            for (_index = 0; _index < _shipsThatLostBeehiveOrDied.Count; _index++)
            {
                _ship = _shipsThatLostBeehiveOrDied[_index];
                ReleaseHealingReservation(_ship, _ship != null && !_ship.IsDead && _ship.Health < _ship.MaxHealth);
            }
            _shipsThatLostBeehiveOrDied.Clear();
            FinalizeIfAssignedShipsAreDone();
        }

        int _oldTsv;
        int _tsvDifference;
        public void HealShips()
        {
            if (Level.HasPlayer)
            {
                _healingCrossBeehives.Clear();
                for (_index = 0; _index < ShipsHealing.Count; _index++)
                {
                    _ship = ShipsHealing[_index];
                    if (_ship != null && _shipsAndBeehives.TryGetValue(_ship.Id, out _beehive) &&
                        _beehive != null && !_beehive.IsDead && _healingCrossBeehives.Add(_beehive))
                    {
                        _beehive.SpawnHealingCross();
                    }
                }
            }
            _healingTimerCount++;
            for (_index = 0; _index < ShipsHealing.Count; _index++)
            {
                _ship = ShipsHealing[_index];
                if (_ship == null || !_shipsAndBeehives.TryGetValue(_ship.Id, out _beehive) || _beehive == null || _beehive.IsDead || _ship.IsDead)
                {
                    _shipsThatLostBeehiveOrDied.Add(_ship);
                    continue;
                }

                _oldTsv = _ship.Tsv;
                _ship.Health += math.min(_ship.MaxHealth - _ship.Health, 50);
                _ship.Tsv = Utilities.CalculateTsv(_ship);
                _tsvDifference = _ship.Tsv - _oldTsv;

                Tsv += _tsvDifference;

                _ship.UpdateHealthBar();
                if (_ship.Health >= _ship.MaxHealth)
                {
                    _shipsThatLostBeehiveOrDied.Add(_ship);
                }
            }
            for (_index = 0; _index < _shipsThatLostBeehiveOrDied.Count; _index++)
            {
                _ship = _shipsThatLostBeehiveOrDied[_index];
                ReleaseHealingReservation(_ship, _ship != null && !_ship.IsDead && _ship.Health < _ship.MaxHealth);
            }
            _shipsThatLostBeehiveOrDied.Clear();
            FinalizeIfAssignedShipsAreDone();
        }

        public override void SetFinalize(string cause)
        {
            _reservedShipsToRelease.Clear();
            _reservedShipsToRelease.UnionWith(ShipsWaitingToHeal);
            _reservedShipsToRelease.UnionWith(ShipsHealing);
            foreach (Ship reservedShip in _reservedShipsToRelease)
            {
                ReleaseHealingReservation(reservedShip);
            }
            _reservedShipsToRelease.Clear();
            Level.CancelTimer(_healingTimer);
            base.SetFinalize(cause);
        }
    }
}
