using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private static int _lastEnemyRightClickFrame = -1;
        private static int _lastEnemyRightClickSquadItemId = int.MinValue;

        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;
            if (_tempCollidingThing.name == "Selection Box" && IsUserControlled)
            {
                Stage.Selector.SelectShip(this);
            }
        }

        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;
            if (_tempCollidingThing.name == "Selection Box" && IsUserControlled)
            {
                Stage.Selector.DeselectShip(this);
            }
        }

        private static bool CanIssueUserAttack(Squad attacker, Squad enemy)
        {
            if (attacker == null || enemy == null || attacker.Level?.Pathfinder == null || !attacker.Level.HasObstacles)
            {
                return true;
            }

            int clearance = int.MaxValue;
            foreach (Ship ship in attacker.GetShips())
            {
                if (ship == null || ship.IsDead || !ship.IsMobile)
                {
                    continue;
                }
                clearance = Mathf.Min(clearance, ship.GetClearance());
            }

            if (clearance == int.MaxValue)
            {
                clearance = ConfigData.MinimumClearance;
            }

            return attacker.Level.Pathfinder.AreStaticallyConnected(attacker.GetPosition(), enemy.GetPosition(), clearance);
        }

        public void Clicked(int mouseButton, bool isCtrlClick = false)
        {
            if (!IsUserControlled && mouseButton == LevelInputManager.RightClick)
            {
                // LevelInputManager can resolve the same right-click twice in one frame: once
                // through its proximity fallback and again through the normal clicked-ship path.
                // The two resolutions can even use different ships from the same enemy squad, so
                // dedupe by target squad rather than by Ship instance.
                int targetSquadItemId = Squad != null ? Squad.ItemId : int.MinValue;
                if (_lastEnemyRightClickFrame == Time.frameCount &&
                    _lastEnemyRightClickSquadItemId == targetSquadItemId)
                {
                    return;
                }

                _lastEnemyRightClickFrame = Time.frameCount;
                _lastEnemyRightClickSquadItemId = targetSquadItemId;

                foreach (Squad selectedSquad in Level.State.GetSelectedSquads())
                {
                    // Reject permanently disconnected targets before UserAggressive() finalizes
                    // the current order, obtains a pooled command, builds targeting queues, or
                    // starts any pathfinding. This keeps an isolated authored pocket from turning
                    // a right-click into a burst of doomed attack work.
                    if (!CanIssueUserAttack(selectedSquad, Squad))
                    {
                        Debug.LogWarning($"Ignoring attack from {selectedSquad} to unreachable enemy {Squad}.");
                        continue;
                    }
                    selectedSquad.UserAggressive(Squad);
                }
            }
            else if (IsUserControlled && mouseButton == LevelInputManager.LeftClick && !Squad.IsImmobile)
            {
                if (isCtrlClick) Level.State.AddSelectedSquad(Squad);
                else Level.State.SelectSquad(Squad);
            }
        }
    }
}
