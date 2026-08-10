namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        /// <summary>
        /// Whether this dead ship wrapper can be returned to its ObjectPool.
        /// Subclasses with delayed callbacks/effects that still require the wrapper
        /// must keep it false until that work has relinquished ownership.
        /// </summary>
        public virtual bool CanReturnToPool()
        {
            if (ProjectilesInFlight.Count > 0)
            {
                return false;
            }

            // Pathfinder workers capture the Ship reference and lifecycle ID while they run.
            // Reusing this wrapper before that request has left the worker/queue lets stale
            // asynchronous work race the next ship lifecycle using the same pooled object.
            return Level?.Pathfinder == null ||
                !Level.Pathfinder.HasOutstandingWorkForShip(this, PathfindingLifecycleId);
        }

        /// <summary>
        /// Called at a Level teardown boundary before release queues are drained.
        /// Subclasses may cancel presentation-only delayed callbacks that must not retain
        /// or later mutate a pooled wrapper after the owning Level is ending/resetting.
        /// </summary>
        public virtual void PrepareForLevelTeardown()
        {
        }
    }
}
