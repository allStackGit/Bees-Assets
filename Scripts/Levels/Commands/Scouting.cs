
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Scouting : Command
    {
        private bool _foundShips;
        List<Scout> Scouts = new List<Scout>();
        HashSet<long> ScoutIds = new HashSet<long>();
        private Vector2 _position, _randomPoint;
        private Vector2 _ten = Vector2.one * 10;
        private ScaledTimer _dropBeaconsTimer = new ScaledTimer();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

            PrepareDamageToSendEntries(1);
            _position = GetSquad().GetPosition();
            _randomPoint = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * GetSquad().MaxSight);
            SetAndMove(_randomPoint);
            CommandFrequency = 5;

            CommandTimer.Reuse(CommandFrequency, Timer, true, true);
            Level.AddTimer(CommandTimer);

            TimeoutTimer.Reuse(300, EndCommand);
            Level.AddTimer(TimeoutTimer);

            if (GetSquad().Side == ConfigData.Configuration.HumanSide)
            {
                List<Ship> ships = GetSquad().GetShips();
                for (int i = 0; i < ships.Count; i++)
                {
                    Ship ship = ships[i];
                    if (ship.ShipType == ConfigData.ShipTypes.Scout)
                    {
                        Scouts.Add((Scout)ship);
                        ScoutIds.Add(ship.Id);
                    }
                }

                if (Scouts.Count > 0)
                {
                    _dropBeaconsTimer.Reuse(ConfigData.MinimumDelayPerBeacon, DropScoutBeacons, true);
                    Level.AddTimer(_dropBeaconsTimer);
                }
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            _foundShips = false;
            Scouts.Clear();
            ScoutIds.Clear();
        }

        private void Timer()
        {
            if (!GetSquad().IsDead && GetSquad().HasReachedDestination)
            {
                _position = GetSquad().GetPosition();
                _randomPoint = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, _ten);
                SetAndMove(_randomPoint);
                GetSquad().Status = $"Moving to random destination to look for ships: {_randomPoint}";
            }
        }

        public void DropScoutBeacons()
        {
            for (int i = Scouts.Count - 1; i >= 0; i--)
            {
                Scout scout = Scouts[i];
                if (scout == null || scout.IsDead)
                {
                    if (scout != null)
                    {
                        ScoutIds.Remove(scout.Id);
                    }
                    Scouts.RemoveAt(i);
                    continue;
                }
                scout.DropBeacon();
            }
        }

        private ScaledTimer _endCommandTimer = new ScaledTimer();
        public void FoundNewShips()
        {
            if (!_foundShips)
            {
                _foundShips = true;
                Level.CancelTimer(TimeoutTimer);
                _endCommandTimer.Reuse(5, EndCommand);
                Level.AddTimer(_endCommandTimer);
            }
        }

        private void EndCommand()
        {
            if (_foundShips)
            {
                SetFinalize("Found ships");
            }
            else
            {
                SetFinalize("Ran out of time");
            }
        }

        public override void SetFinalize(string cause)
        {
            Level.CancelTimer(_dropBeaconsTimer);
            Level.CancelTimer(_endCommandTimer);
            base.SetFinalize(cause);
        }
    }
}