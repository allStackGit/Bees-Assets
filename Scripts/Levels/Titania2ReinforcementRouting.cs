using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private void EnsureTitania2ReinforcementRoute(ref Vector2 startingPosition, ref Vector2 nextPosition)
        {
            if (Pathfinder.AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance))
            {
                return;
            }

            Vector2 requestedEntry = nextPosition;
            bool found = false;
            float bestDistance = float.MaxValue;
            Vector2 bestSpawn = startingPosition;
            Vector2 bestEntry = nextPosition;

            const float insideDistance = 32f;
            const float outsideDistance = 80f;
            const float edgeMargin = 28f;
            const float scanStep = 20f;

            // Scan all four map edges. A candidate is accepted only when the pathfinder's
            // authored/static map says it belongs to the same connected region as Titania.
            for (float x = MinX + edgeMargin; x <= MaxX - edgeMargin; x += scanStep)
            {
                ConsiderTitania2Entry(
                    new Vector2(x, MinY - outsideDistance),
                    new Vector2(x, MinY + insideDistance),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestSpawn, ref bestEntry);
                ConsiderTitania2Entry(
                    new Vector2(x, MaxY + outsideDistance),
                    new Vector2(x, MaxY - insideDistance),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestSpawn, ref bestEntry);
            }

            for (float y = MinY + edgeMargin; y <= MaxY - edgeMargin; y += scanStep)
            {
                ConsiderTitania2Entry(
                    new Vector2(MinX - outsideDistance, y),
                    new Vector2(MinX + insideDistance, y),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestSpawn, ref bestEntry);
                ConsiderTitania2Entry(
                    new Vector2(MaxX + outsideDistance, y),
                    new Vector2(MaxX - insideDistance, y),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestSpawn, ref bestEntry);
            }

            if (found)
            {
                Debug.LogWarning($"Beenoculars reinforcement entry {requestedEntry} is in a sealed map region; rerouting to {bestEntry}.");
                startingPosition = bestSpawn;
                nextPosition = bestEntry;
                return;
            }

            // Never strand a Bee squad in a sealed pocket. If no connected edge opening can be
            // found, place it in the playable region as a last-resort mission-safe fallback.
            if (Pathfinder.TryFindNearestValidDestination(Titania2Center + new Vector2(0f, -60f), ConfigData.MinimumClearance, out Vector2 fallback))
            {
                Debug.LogError($"Beenoculars found no connected map-edge reinforcement lane; spawning inside the playable arena at {fallback}.");
                startingPosition = fallback;
                nextPosition = fallback;
            }
        }

        private void ConsiderTitania2Entry(
            Vector2 spawnPoint,
            Vector2 entryPoint,
            Vector2 requestedEntry,
            ref bool found,
            ref float bestDistance,
            ref Vector2 bestSpawn,
            ref Vector2 bestEntry)
        {
            if (!Pathfinder.CanOccupyDestination(entryPoint, ConfigData.MinimumClearance) ||
                !Pathfinder.AreStaticallyConnected(entryPoint, Titania2Center, ConfigData.MinimumClearance))
            {
                return;
            }

            Vector2 worldSpawn = PathfinderObstacleScope.LevelToWorld(this, spawnPoint);
            Vector2 worldEntry = PathfinderObstacleScope.LevelToWorld(this, entryPoint);
            if (Physics2D.Linecast(worldSpawn, worldEntry, ConfigData.ObstaclesLayerMask).collider != null)
            {
                return;
            }

            float distance = (entryPoint - requestedEntry).sqrMagnitude;
            if (!found || distance < bestDistance)
            {
                found = true;
                bestDistance = distance;
                bestSpawn = spawnPoint;
                bestEntry = entryPoint;
            }
        }
    }
}
