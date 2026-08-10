using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
        /// <summary>
        /// Returns true while this pathfinder still owns queued or worker-thread work for
        /// the specified ship lifecycle. A pooled Ship wrapper must not be reused until
        /// every path request that captured that lifecycle has released its reference.
        /// </summary>
        public bool HasOutstandingWorkForShip(Ship ship, int lifecycleId)
        {
            if (ship == null)
            {
                return false;
            }

            for (int threadIndex = 0; threadIndex < IsThreadActive.Length; threadIndex++)
            {
                if (IsThreadActive[threadIndex] &&
                    ReferenceEquals(Ships[threadIndex], ship) &&
                    LifecycleIds[threadIndex] == lifecycleId)
                {
                    return true;
                }
            }

            foreach (PathWaiting request in PathsWaiting)
            {
                if (ReferenceEquals(request.Ship, ship) && request.LifecycleId == lifecycleId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
