
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class ClosestFriendly : Command
    {
        private Squad _closestFriendlySquad;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            _closestFriendlySquad = GetSquad().GetClosestValidFriendlySquad();
            if (_closestFriendlySquad != null)
            {
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                CommandTimer.Reuse(CommandFrequency, Timer, true);
                Level.AddTimer(CommandTimer);
                TimeoutTimer.Reuse(ConfigData.Configuration.AISquadFollowingTime, Timeout);
                Level.AddTimer(TimeoutTimer);
            }
            else
            {
                SetFinalize("There is no friendly squad to follow");
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            _closestFriendlySquad = null;
        }

        private bool IsAnyShipPathfinding()
        {
            foreach (Ship ship in GetSquad().GetShips())
            {
                if (ship.IsPathfinding)
                {
                    return true;
                }
            }
            return false;
        }

        Vector2 _timer_position;
        private void Timer()
        {
            Squad squad = GetSquad();
            if (squad.IsDead)
            {
                return;
            }

            if (_closestFriendlySquad == null || _closestFriendlySquad.IsDead)
            {
                SetFinalize("The friendly squad to follow is gone or dead");
                return;
            }

            _timer_position = _closestFriendlySquad.GetPosition();
            squad.Status = squad.HasReachedDestination
                ? $"Trying to catch up to friendly squad #{_closestFriendlySquad.SquadNumber}"
                : $"Following friendly squad #{_closestFriendlySquad.SquadNumber}";

            if (!IsAnyShipPathfinding())
            {
                SetAndMove(_timer_position);
            }
        }
    }
}