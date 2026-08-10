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
    }
}
