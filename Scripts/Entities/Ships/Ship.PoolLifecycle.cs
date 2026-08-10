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
            return ProjectilesInFlight.Count == 0;
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
