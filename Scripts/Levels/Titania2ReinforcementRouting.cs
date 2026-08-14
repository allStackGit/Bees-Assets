using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private const float Titania2OffscreenSpawnDistance = 80f;

        private void EnsureTitania2ReinforcementRoute(ref Vector2 startingPosition, ref Vector2 nextPosition)
        {
            // Beenoculars now deliberately uses a completely clear battlefield. In that setup
            // Level does not build an obstacle Pathfinder, and the authored off-map -> in-map
            // reinforcement route needs no static-connectivity repair. Keep this helper strictly
            // for obstacle-bearing variants and fail open if pathfinding state is unavailable.
            if (!HasObstacles || Pathfinder == null)
            {
                return;
            }

            Vector2 requestedEntry = nextPosition;

            if (Pathfinder.CanOccupyDestination(nextPosition, ConfigData.MinimumClearance) &&
                Pathfinder.AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance))
            {
                startingPosition = GetTitania2OffscreenSpawn(nextPosition);
                return;
            }

            bool found = false;
            float bestDistance = float.MaxValue;
            Vector2 bestEntry = nextPosition;

            const float insideDistance = 32f;
            const float edgeMargin = 28f;
            const float scanStep = 20f;

            for (float x = MinX + edgeMargin; x <= MaxX - edgeMargin; x += scanStep)
            {
                ConsiderTitania2Entry(
                    new Vector2(x, MinY + insideDistance),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestEntry);
                ConsiderTitania2Entry(
                    new Vector2(x, MaxY - insideDistance),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestEntry);
            }

            for (float y = MinY + edgeMargin; y <= MaxY - edgeMargin; y += scanStep)
            {
                ConsiderTitania2Entry(
                    new Vector2(MinX + insideDistance, y),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestEntry);
                ConsiderTitania2Entry(
                    new Vector2(MaxX - insideDistance, y),
                    requestedEntry,
                    ref found, ref bestDistance, ref bestEntry);
            }

            if (found)
            {
                Debug.LogWarning($"Beenoculars reinforcement entry {requestedEntry} is unusable or sealed; rerouting to connected entry {bestEntry}.");
                nextPosition = bestEntry;
                startingPosition = GetTitania2OffscreenSpawn(bestEntry);
                return;
            }

            // If the authored map exposes no connected edge opening, fail safe inside the
            // playable arena rather than manufacturing an off-screen route through a wall.
            if (Pathfinder.TryFindNearestValidDestination(Titania2Center + new Vector2(0f, -60f), ConfigData.MinimumClearance, out Vector2 fallback) &&
                Pathfinder.AreStaticallyConnected(fallback, Titania2Center, ConfigData.MinimumClearance))
            {
                Debug.LogError($"Beenoculars found no connected map-edge reinforcement lane; spawning inside the playable arena at {fallback}.");
                startingPosition = fallback;
                nextPosition = fallback;
                return;
            }

            Debug.LogError("Beenoculars could not resolve a connected reinforcement lane; spawning at Titania as a final safety fallback.");
            startingPosition = Titania2Center;
            nextPosition = Titania2Center;
        }

        private Vector2 GetTitania2OffscreenSpawn(Vector2 entryPoint)
        {
            float leftDistance = Mathf.Abs(entryPoint.x - MinX);
            float rightDistance = Mathf.Abs(MaxX - entryPoint.x);
            float bottomDistance = Mathf.Abs(entryPoint.y - MinY);
            float topDistance = Mathf.Abs(MaxY - entryPoint.y);
            float nearest = Mathf.Min(leftDistance, rightDistance, bottomDistance, topDistance);

            if (nearest == leftDistance)
            {
                return new Vector2(MinX - Titania2OffscreenSpawnDistance, entryPoint.y);
            }
            if (nearest == rightDistance)
            {
                return new Vector2(MaxX + Titania2OffscreenSpawnDistance, entryPoint.y);
            }
            if (nearest == bottomDistance)
            {
                return new Vector2(entryPoint.x, MinY - Titania2OffscreenSpawnDistance);
            }
            return new Vector2(entryPoint.x, MaxY + Titania2OffscreenSpawnDistance);
        }

        private void ConsiderTitania2Entry(
            Vector2 entryPoint,
            Vector2 requestedEntry,
            ref bool found,
            ref float bestDistance,
            ref Vector2 bestEntry)
        {
            if (!Pathfinder.CanOccupyDestination(entryPoint, ConfigData.MinimumClearance) ||
                !Pathfinder.AreStaticallyConnected(entryPoint, Titania2Center, ConfigData.MinimumClearance))
            {
                return;
            }

            Vector2 worldEntry = PathfinderObstacleScope.LevelToWorld(this, entryPoint);
            if (Physics2D.OverlapCircle(worldEntry, 18f, ConfigData.ObstaclesLayerMask) != null)
            {
                return;
            }

            float distance = (entryPoint - requestedEntry).sqrMagnitude;
            if (!found || distance < bestDistance)
            {
                found = true;
                bestDistance = distance;
                bestEntry = entryPoint;
            }
        }
    }
}
