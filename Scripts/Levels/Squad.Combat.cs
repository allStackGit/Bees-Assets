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
            IsMinionSquad = false;
            enabled = false;
        }

        public Squad GetClosestEnemySquad()
        {
            Vector2 origin = GetPosition();
            Squad closest = null;
            float closestDistance = float.MaxValue;

            if (Side == ConfigData.Configuration.UserSide && Level.HasPlayer)
            {
                List<Squad> squads = Level.State.Squads;
                for (int i = 0; i < squads.Count; i++)
                {
                    Squad squad = squads[i];
                    if (squad.Side == Side || squad.IsDead)
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

            foreach (Ship visibleShip in Level.State.GetShipsVisibleToHiveMind(Side))
            {
                Squad squad = visibleShip?.Squad;
                if (squad == null || squad.IsDead)
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

        public Squad GetClosestValidFriendlySquad()
        {
            Vector2 origin = GetPosition();
            Squad closest = null;
            float closestDistance = float.MaxValue;
            List<Squad> squads = Level.State.Squads;
            for (int i = 0; i < squads.Count; i++)
            {
                Squad squad = squads[i];
                if (squad.Side != Side || squad.IsDead || squad == this ||
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

        public int GetMaximumRange()
        {
            List<Ship> ships = GetShips();
            int maximumRange = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].MaxRange > maximumRange)
                {
                    maximumRange = ships[i].MaxRange;
                }
            }
            return maximumRange;
        }

        public bool AreAllShipsDefenseless()
        {
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].Firepower != 0)
                {
                    return false;
                }
            }
            return true;
        }

        public List<Ship> GetEnemyShips()
        {
            return Level.State.GetShipsVisibleToHiveMind(Side).ToList();
        }

        public List<Ship> GetFriendlyShips()
        {
            return Level.State.GetShips(Side);
        }

        private readonly List<Ship> _potentialEnemyShips = new List<Ship>();
        private readonly Dictionary<long, float> _combatDistanceKeys = new Dictionary<long, float>();
        private readonly List<Ship> _enemies = new List<Ship>();

        private int CompareCombatDistance(Ship a, Ship b)
        {
            int comparison = _combatDistanceKeys[a.Id].CompareTo(_combatDistanceKeys[b.Id]);
            if (comparison != 0) return comparison;
            comparison = a.ShipType.CompareTo(b.ShipType);
            if (comparison != 0) return comparison;
            return a.Id.CompareTo(b.Id);
        }

        public List<Ship> GetPotentialEnemies(Squad target)
        {
            Vector2 origin = GetPosition();
            _potentialEnemyShips.Clear();
            _combatDistanceKeys.Clear();
            foreach (Ship ship in Level.State.GetShipsVisibleToHiveMind(Side))
            {
                _potentialEnemyShips.Add(ship);
                _combatDistanceKeys[ship.Id] = ship.DistanceToPoint(origin);
            }
            _potentialEnemyShips.Sort(CompareCombatDistance);

            _enemies.Clear();
            for (int i = 0; i < _potentialEnemyShips.Count && _enemies.Count < 64; i++)
            {
                Ship ship = _potentialEnemyShips[i];
                if (ship.Squad == target)
                {
                    _enemies.Add(ship);
                }
            }

            for (int i = 0; i < _potentialEnemyShips.Count && _enemies.Count < 64; i++)
            {
                Ship potentialEnemy = _potentialEnemyShips[i];
                if (potentialEnemy.Squad != target && potentialEnemy.IsAnySquadShipWithinRange(this))
                {
                    _enemies.Add(potentialEnemy);
                }
            }

            return _enemies;
        }

        private readonly List<Ship> _allies = new List<Ship>();
        private int _limit;
        public List<Ship> GetPotentialAllies(Squad target)
        {
            _limit = Math.Max(0, 64 - GetShipsForMatchup().Count);
            _allies.Clear();
            if (_limit == 0 || target == null)
            {
                return _allies;
            }

            Vector2 targetOrigin = target.GetPosition();
            _combatDistanceKeys.Clear();
            List<Ship> ships = Level.State.Ships;
            for (int i = 0; i < ships.Count; i++)
            {
                Ship potentialAlly = ships[i];
                if (potentialAlly.Side != Side || this == potentialAlly.Squad ||
                    !potentialAlly.IsAnySquadShipWithinRange(target))
                {
                    continue;
                }
                _allies.Add(potentialAlly);
                _combatDistanceKeys[potentialAlly.Id] = potentialAlly.DistanceToPoint(targetOrigin);
            }
            _allies.Sort(CompareCombatDistance);
            if (_allies.Count > _limit)
            {
                _allies.RemoveRange(_limit, _allies.Count - _limit);
            }
            return _allies;
        }
    }
}