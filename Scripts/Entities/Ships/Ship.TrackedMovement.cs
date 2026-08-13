using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private const float TrackedTargetPathReplanInterval = 1f;
        private const float TrackedTargetPathReplanDistance = Pathfinder.Scale * 2f;
        private float _nextTrackedTargetPathReplanTime;
        private int _trackedTargetPathLifecycleId = int.MinValue;

        /// <summary>
        /// Updates a destination that belongs to a moving target without treating every command
        /// timer tick as a brand-new movement order. Active searches, useful paths, and the
        /// failed-search retry timer retain ownership until there is a meaningful reason to
        /// replace them.
        /// </summary>
        public void MoveToTrackedPoint(Vector2 destination)
        {
            if (_trackedTargetPathLifecycleId != PathfindingLifecycleId)
            {
                _trackedTargetPathLifecycleId = PathfindingLifecycleId;
                _nextTrackedTargetPathReplanTime = 0f;
            }

            if (CannotChangeMovementOrders)
            {
                // Preserve MoveToPoint's existing queued-order semantics.
                MoveToPoint(destination);
                return;
            }

            destination = CanOverrideBounds ? destination : Level.ForceBounds(destination);

            // A running A* worker still owns the request. Invalidating it does not cancel its
            // Task.Run work, so a recurring attack timer must not create an obsolete-worker tail.
            if (IsPathfinding)
            {
                return;
            }

            // A failed search deliberately owns a two-second retry window. The old Aggressive
            // path bypassed that backoff every 0.25 seconds because IsPathfinding was already
            // false while this flag was true.
            if (_tryingToFindPathAgain)
            {
                return;
            }

            float endpointMovement = Vector2.Distance(destination, FinalDestination);
            float meaningfulMovement = Mathf.Max(TrackedTargetPathReplanDistance, ConfigData.CloseEnoughCoordinateVariance);

            if (IsFollowingPath)
            {
                if (endpointMovement < meaningfulMovement || Time.time < _nextTrackedTargetPathReplanTime)
                {
                    return;
                }

                _nextTrackedTargetPathReplanTime = Time.time + TrackedTargetPathReplanInterval;
            }
            else if (HasTargetCoordinates && endpointMovement < meaningfulMovement)
            {
                // Direct movement on obstacle-free maps should also avoid rewriting virtually
                // identical destinations four times per second as a target jitters in formation.
                return;
            }

            MoveToPoint(destination);
        }
    }
}
