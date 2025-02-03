
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
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(ConfigData.CommandTypes.Scouting, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);

            PrepareDamageToSendEntries("closest");
            Vector2 position = Squad.GetPosition();
            Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * Squad.MaxSight);
            SetAndMove(randomCoordinates);
            CommandFrequency = 5;
            InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            Invoke(nameof(EndCommand), ConfigData.Configuration.AISquadPatrolTime);

            if (Squad.Side == ConfigData.Configuration.HumanSide && ConfigData.Configuration.UserSide == Squad.Side)
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
                Vector2 position = Squad.GetPosition();
                Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
                SetAndMove(randomCoordinates);
                Squad.Status = $"Moving to random destination to look for ships: {randomCoordinates}";

            }

        }

        public void DropScoutBeacons()
        {
            List<Scout> scoutsToRemove = new List<Scout>();
            Scouts.ForEach((scout) =>
            {
                if (scout != null && !scout.IsDead)
                {
                    scout.DropBeacon();
                }
                else
                {
                    scoutsToRemove.Add(scout);
                }
            });

            if (scoutsToRemove.Count > 0)
            {
                Scouts = Scouts.Except(scoutsToRemove).ToList();
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