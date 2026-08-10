using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;

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
            return Level.State.GetSquadsVisibleToHiveMind(Side)
                .OrderBy(squad => squad.DistanceToPoint(GetPosition()))
                .FirstOrDefault();
        }

        public Squad GetClosestValidFriendlySquad()
        {
            _tempSquads = Level.State.GetSquadsBySide(Side)
                .Where(squad => squad != this &&
                    (!squad.HasCommand || squad.GetCommand() == null ||
                     squad.GetCommand().CommandType != ConfigData.CommandTypes.ClosestFriendly))
                .ToList();
            return _tempSquads.OrderBy(squad => squad.DistanceToPoint(GetPosition())).FirstOrDefault();
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
            _tempShips = GetEnemyShips();
            _enemies = _tempShips.Where(s => s.Squad == target).Take(64).ToList();

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
            _tempShips.Clear();
            _allies = GetFriendlyShips();
            _limit = Math.Max(0, 64 - GetShipsForMatchup().Count);

            foreach (Ship potentialAlly in _allies)
            {
                if (this != potentialAlly.Squad && _tempShips.Count < _limit &&
                    potentialAlly.IsAnySquadShipWithinRange(target))
                {
                    _tempShips.Add(potentialAlly);
                }
            }

            return _tempShips;
        }
    }
}