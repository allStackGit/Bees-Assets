
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
        private Vector2 _position, _randomPoint;
        private Vector2 _ten = Vector2.one * 10;
        public override void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);

            PrepareDamageToSendEntries("closest");
            _position = Squad.GetPosition();
            _randomPoint = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * Squad.MaxSight);
            SetAndMove(_randomPoint);
            CommandFrequency = 5;
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            Invoke(nameof(EndCommand), ConfigData.Configuration.AISquadPatrolTime);

            if (Squad.Side == ConfigData.Configuration.HumanSide)
            {
                Squad.GetShips().ForEach((ship) =>
                {
                    if (ship.ShipType == ConfigData.ShipTypes.Scout)
                    {
                        Scouts.Add((Scout)ship);
                    }
                });

                if (Scouts.Count > 0)
                {
                    InvokeRepeating(nameof(DropScoutBeacons), ConfigData.MinimumDelayPerBeacon, ConfigData.MinimumDelayPerBeacon);
                }
            }

        }
        public override void ClearData()
        {
            base.ClearData();
            _foundShips = false;
            Scouts.Clear();
        }
        private void Timer()
        {
            if (!Squad.IsDead && Squad.HasReachedDestination)
            {
                _position = Squad.GetPosition();
                _randomPoint = Utilities.RandomCoordinate(Level, _position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, _ten);
                SetAndMove(_randomPoint);
                Squad.Status = $"Moving to random destination to look for ships: {_randomPoint}";

            }

        }
        private List<Scout> _scoutsToRemove = new List<Scout>();
        public void DropScoutBeacons()
        {
            Scouts.ForEach((scout) =>
            {
                if (!scout.IsDead)
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

        public void FoundShips()
        {
            _foundShips = true;
            Invoke(nameof(EndCommand), 5);
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


    }
}