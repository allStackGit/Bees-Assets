using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Heal : Command
    {

        public List<Beehive> TargetBeehives;
        public List<Ship> ShipsWaitingToHeal = new List<Ship>();
        public List<Ship> ShipsHealing = new List<Ship>();
        public bool IsHealing;
        private int _spotsAvailable;
        private int _index;
        private int _indexJ;
        private Beehive _beehive;
        private Queue<Ship> _shipsThatNeedBeehive;
        private Ship _ship;
        private Dictionary<long, Beehive> _shipsAndBeehives = new Dictionary<long, Beehive>();
        private int _healingTimerCount;
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, List<Beehive> beehives)
        {
            TargetBeehives = beehives.Where((b) => b != null && !b.IsDead).ToList();
            if (TargetBeehives.Count > 0)
            {
                _shipsThatNeedBeehive = new Queue<Ship>(GetSquad().GetShips()
                    .Where((s) => !s.IsDead && s.Health < s.MaxHealth)
                    .OrderBy((s) => s.Health - s.OriginalHealth));

                if (_shipsThatNeedBeehive.Count == 0)
                {
                    SetFinalize("There are no damaged ships to heal");
                    return;
                }

                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

                for (_index = 0; _index < TargetBeehives.Count && _shipsThatNeedBeehive.Count > 0; _index++)
                {
                    _beehive = TargetBeehives[_index];
                    _spotsAvailable = 4 - _beehive.ShipsHealingHere.Count;

                    for (_indexJ = 0; _indexJ < _spotsAvailable && _shipsThatNeedBeehive.Count > 0; _indexJ++)
                    {
                        _ship = _shipsThatNeedBeehive.Dequeue();

                        ShipsWaitingToHeal.Add(_ship);
                        _beehive.ShipsHealingHere.Add(_ship);
                        _shipsAndBeehives.Add(_ship.Id, _beehive);
                    }
                }

                if (ShipsWaitingToHeal.Count == 0)
                {
                    SetFinalize("There are no available beehive healing slots");
                    return;
                }

                GetSquad().Status = $"Moving to {TargetBeehives.Count} beehives to heal";
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
            ShipsWaitingToHeal.Clear();
            ShipsHealing.Clear();
            _shipsAndBeehives.Clear();
            _shipsThatLostBeehiveOrDied.Clear();
            _healingTimerCount = 0;
            IsHealing = false;
        }

        private List<Ship> _shipsThatLostBeehiveOrDied = new List<Ship>();
        public void Timer()
        {
            if (!GetSquad().IsDead)
            {
                TargetBeehives = TargetBeehives.Where((b) => b != null && !b.IsDead).ToList();
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

        private void ReleaseHealingReservation(Ship ship)
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
        }

        private void FinalizeIfAssignedShipsAreDone()
        {
            if (!IsDead && ShipsWaitingToHeal.Count == 0 && ShipsHealing.Count == 0)
            {
                SetFinalize("All assigned ships finished healing or became unavailable");
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

                _ship.MoveToPoint(_beehive.GetPosition());
            }

            for (_index = 0; _index < _shipsThatLostBeehiveOrDied.Count; _index++)
            {
                ReleaseHealingReservation(_shipsThatLostBeehiveOrDied[_index]);
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
                ShipsHealing
                    .Where((s) => s != null && _shipsAndBeehives.ContainsKey(s.Id))
                    .Select((s) => _shipsAndBeehives[s.Id])
                    .Where((b) => b != null && !b.IsDead)
                    .Distinct()
                    .ToList()
                    .ForEach((b) => b.SpawnHealingCross());
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
                ReleaseHealingReservation(_shipsThatLostBeehiveOrDied[_index]);
            }
            _shipsThatLostBeehiveOrDied.Clear();
            FinalizeIfAssignedShipsAreDone();
        }

        public override void SetFinalize(string cause)
        {
            foreach (Ship reservedShip in ShipsWaitingToHeal.Concat(ShipsHealing).Distinct().ToList())
            {
                ReleaseHealingReservation(reservedShip);
            }
            Level.CancelTimer(_healingTimer);
            base.SetFinalize(cause);
        }
    }
}
