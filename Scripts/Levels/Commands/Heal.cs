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
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, List<Beehive> beehives)
        {
            if (beehives.Count > 0)
            {
                TargetBeehives = beehives;
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

                _shipsThatNeedBeehive = new Queue<Ship>(GetSquad().GetShips().OrderBy((s) => s.Health - s.OriginalHealth));

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

                GetSquad().Status = $"Moving to {TargetBeehives.Count} beehives to heal";
                Timer();
                if (!IsDead) // The previous run of Timer() could have killed the command
                {
                    CommandTimer.Reuse(CommandFrequency, Timer, true);
                    Level.AddTimer(CommandTimer);

                    if (IsHiveMindCommand)
                    {
                        TimeoutTimer.Reuse(300, Timeout);
                        Level.AddTimer(TimeoutTimer);
                    }
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
            IsHealing = false;
        }

        private List<Ship> _shipsThatLostBeehiveOrDied = new List<Ship>();
        public void Timer()
        {
            if (!GetSquad().IsDead)
            {
               
                TargetBeehives = TargetBeehives.Where((b) => !b.IsDead).ToList();
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

        public void MoveToBeehives()
        {
            for (_index = 0; _index < ShipsWaitingToHeal.Count; _index++)
            {
                _ship = ShipsWaitingToHeal[_index];
                _beehive = _shipsAndBeehives[_ship.Id];

                if (!_beehive.IsDead && !_ship.IsDead)
                {
                    //Debug.Log($"Moving {_ship.Name} to {_beehive.Name} to heal");
                    _ship.MoveToPoint(_beehive.GetPosition());
                }
                else
                {
                    _shipsThatLostBeehiveOrDied.Add(_ship);
                }
            }

            for (_index = 0; _index < _shipsThatLostBeehiveOrDied.Count; _index++)
            {
                ShipsWaitingToHeal.Remove(_shipsThatLostBeehiveOrDied[_index]);
            }
            _shipsThatLostBeehiveOrDied.Clear();
        }

        int _oldTsv;
        int _tsvDifference;
        public void HealShips()
        {
            for (_index = 0; _index < ShipsHealing.Count; _index++)
            {
                _ship = ShipsHealing[_index];
                _beehive = _shipsAndBeehives[_ship.Id];

                if (!_beehive.IsDead && !_ship.IsDead)
                {
                    _oldTsv = _ship.Tsv;
                    _ship.Health += math.min(_ship.MaxHealth - _ship.Health, 50);
                    _ship.Tsv = Utilities.CalculateTsv(_ship);
                    _tsvDifference = _ship.Tsv - _oldTsv;

                    Tsv += _tsvDifference;

                    _ship.UpdateHealthBar();
                }
                else
                {
                    _shipsThatLostBeehiveOrDied.Add(_ship);
                }
            }
            for (_index = 0; _index < _shipsThatLostBeehiveOrDied.Count; _index++)
            {
                ShipsHealing.Remove(_shipsThatLostBeehiveOrDied[_index]);
            }
            _shipsThatLostBeehiveOrDied.Clear();
        }

        public override void SetFinalize(string cause)
        {
            for (_index = 0; _index < ShipsHealing.Count; _index++)
            {
                _ship = ShipsHealing[_index];
                _shipsAndBeehives[_ship.Id].ShipsHealingHere.Remove(_ship);
            }
            for (_index = 0; _index < ShipsWaitingToHeal.Count; _index++)
            {
                _ship = ShipsWaitingToHeal[_index];
                _shipsAndBeehives[_ship.Id].ShipsHealingHere.Remove(_ship);
            }
            Level.CancelTimer(_healingTimer);
            base.SetFinalize(cause);
        }
    }
}