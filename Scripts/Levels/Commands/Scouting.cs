
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Scouting : Command
    {
        /*
        Sends the squad towards a random spot on the map in search of ships
         */
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

            TimeoutTimer.Reuse(ConfigData.Configuration.AISquadPatrolTime, EndCommand);
            Level.AddTimer(TimeoutTimer);


            if (GetSquad().Side == ConfigData.Configuration.HumanSide)
            {
                GetSquad().GetShips().ForEach((ship) =>
                {
                    if (ship.ShipType == ConfigData.ShipTypes.Scout)
                    {
                        Scouts.Add((Scout)ship);
                        ScoutIds.Add(ship.Id);
                    }
                });

                if (Scouts.Count > 0)
                {
                    _dropBeaconsTimer.Reuse(ConfigData.MinimumDelayPerBeacon, DropScoutBeacons, true);
                    //InvokeRepeating(nameof(DropScoutBeacons), ConfigData.MinimumDelayPerBeacon, ConfigData.MinimumDelayPerBeacon);
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
        private List<Scout> _scoutsToRemove = new List<Scout>();
        public void DropScoutBeacons()
        {
            Scouts.ForEach((scout) =>
            {
                if (!scout.IsDead && ScoutIds.Contains(scout.Id)) // Checking the scout ids ensures that this scout didn't die and then become a new ship with a new Id
                {
                    scout.DropBeacon();
                }
                else
                {
                    _scoutsToRemove.Add(scout);
                }
            });

            if (_scoutsToRemove.Count > 0)
            {
                Scouts = Scouts.Except(_scoutsToRemove).ToList();
                _scoutsToRemove.Clear();
            }
        }

        private ScaledTimer _endCommandTimer = new ScaledTimer();
        public void FoundShips()
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
                //Debug.Log($"Ending scouting command for {Squad.Name} because we found ships");
                SetFinalize("Found ships");
            }
            else
            {
                SetFinalize("Ran out of time");
                //Debug.Log($"Ending scouting command for {Squad.Name} because we ran out of time");
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