using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        private void CheckChase()
        {
            if (_shouldChase && GetCommand()?.CommandType != ConfigData.CommandTypes.Aggressive && !Level.State.GameOver)
            {
                _tempSquad = GetClosestEnemySquad();
                if (_tempSquad != null && CanSeeSquad(_tempSquad))
                {
                    UserAggressive(_tempSquad);
                }
            }
        }

        public void Kill(bool endKill = false)
        {
            if (IsDead)
            {
                return;
            }

            IsDead = true;

            if (!endKill)
            {
                if (IsUserControlled)
                {
                    DeactivateSquadBox();
                }

                if (Level.State.IsSideKilled(Side) &&
                    (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign || ConfigData.IsTestingLevel))
                {
                    Level.State.GameOver = true;
                }
            }

            // Scripted queue objects have already been Setup but are not active commands.
            // Release them before finalizing the active command so squad death cannot leak
            // pooled commands or advance campaign scripting after this squad is gone.
            CancelScriptedCommandQueue();

            if (HasCommand && GetCommand() != null)
            {
                GetCommand().SquadKilled();
            }

            if (IsUserControlled)
            {
                if (HasSquadTab)
                {
                    SquadTab.DisableTab();
                }
                if (HasSquadBox && SquadBox != null)
                {
                    SquadBox.SetActive(false);
                }
                Level.State.DeselectSquad(this);
            }

            Level.CancelTimer(_checkChaseTimer);
            Level.State.RemoveSquad(this);
            // General minion squads share the ordinary SquadPool. Keep the role intact
            // through deregistration so GameState can respect its ownership semantics,
            // then clear it before the wrapper can be reused as a normal squad.
            IsMinionSquad = false;
            enabled = false;
        }

        public Squad GetClosestEnemySquad()
        {
            Vector2 origin = GetPosition();
            Squad closest = null;
            float closestDistance = float.MaxValue;
            foreach (Squad squad in Level.State.GetSquadsVisibleToHiveMind(Side))
            {
                float distance = squad.DistanceToPoint(origin);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = squad;
                }
            }
            return closest;
        }

        public Squad GetClosestValidFriendlySquad()
        {
            Vector2 origin = GetPosition();
            Squad closest = null;
            float closestDistance = float.MaxValue;
            foreach (Squad squad in Level.State.GetSquadsBySide(Side))
            {
                if (squad == this ||
                    (squad.HasCommand && squad.GetCommand() != null &&
                     squad.GetCommand().CommandType == ConfigData.CommandTypes.ClosestFriendly))
                {
                    continue;
                }

                float distance = squad.DistanceToPoint(origin);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = squad;
                }
            }
            return closest;
        }

        public List<Ship> GetEnemyShips()
        {
            return Level.State.GetShipsVisibleToHiveMind(Side).ToList();
        }

        public List<Ship> GetFriendlyShips()
        {
            return Level.State.GetShips(Side);
        }

        private List<Ship> _enemies;
        public List<Ship> GetPotentialEnemies(Squad target)
        {
            Vector2 origin = GetPosition();
            _tempShips = GetEnemyShips()
                .OrderBy(ship => ship.DistanceToPoint(origin))
                .ThenBy(ship => ship.ShipType)
                .ThenBy(ship => ship.Id)
                .ToList();

            // GetShipsVisibleToHiveMind is set-backed. Never let its enumeration order
            // decide which ships enter the 64-ship Hive Mind payload, or equivalent
            // tactical states can hash to different matchups under high density.
            _enemies = _tempShips.Where(ship => ship.Squad == target).Take(64).ToList();

            foreach (Ship potentialEnemy in _tempShips)
            {
                if (potentialEnemy.Squad != target && _enemies.Count < 64 &&
                    potentialEnemy.IsAnySquadShipWithinRange(this))
                {
                    _enemies.Add(potentialEnemy);
                }
            }

            return _enemies;
        }

        private List<Ship> _allies;
        private int _limit;
        public List<Ship> GetPotentialAllies(Squad target)
        {
            _limit = Math.Max(0, 64 - GetShipsForMatchup().Count);
            if (_limit == 0 || target == null)
            {
                return new List<Ship>();
            }

            Vector2 targetOrigin = target.GetPosition();
            // Take(_limit) enforces the same strict cap as the former _tempShips.Count < _limit loop.
            _allies = GetFriendlyShips()
                .Where(potentialAlly => this != potentialAlly.Squad &&
                    potentialAlly.IsAnySquadShipWithinRange(target))
                .OrderBy(potentialAlly => potentialAlly.DistanceToPoint(targetOrigin))
                .ThenBy(potentialAlly => potentialAlly.ShipType)
                .ThenBy(potentialAlly => potentialAlly.Id)
                .Take(_limit)
                .ToList();

            return _allies;
        }
    }
}
