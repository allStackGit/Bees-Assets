using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private void EnsureTitania2ReinforcementRoute(ref Vector2 startingPosition, ref Vector2 nextPosition)
        {
            Vector2 requestedEntry = nextPosition;

            // Reinforcements used to spawn outside the map and then move to nextPosition.
            // Squad.SetStartingPosition() is obstacle-aware, however, so an off-map coordinate is
            // relocated to the nearest place where the formation fits. On Beenoculars that can be
            // a sealed pocket near an edge. Never feed an off-map spawn into generic formation
            // placement: if the requested entry is connected to Titania, spawn directly there.
            if (Pathfinder.CanOccupyDestination(nextPosition, ConfigData.MinimumClearance) &&
                Pathfinder.AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance))
            {
                startingPosition = nextPosition;
                return;
            }

            bool found = false;
            float bestDistance = float.MaxValue;
            Vector2 bestEntry = nextPosition;

            const float insideDistance = 32f;
            const float edgeMargin = 28f;
            const float scanStep = 20f;

            // Scan all four map edges. A candidate is accepted only when the pathfinder's
            // authored/static map says it belongs to the same connected region as Titania.
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
                Debug.LogWarning($"Beenoculars reinforcement entry {requestedEntry} is unusable or sealed; rerouting spawn to connected entry {bestEntry}.");
                startingPosition = bestEntry;
                nextPosition = bestEntry;
                return;
            }

            // Never strand a Bee squad in a sealed pocket. If no connected edge opening can be
            // found, place it in the playable region as a last-resort mission-safe fallback.
            if (Pathfinder.TryFindNearestValidDestination(Titania2Center + new Vector2(0f, -60f), ConfigData.MinimumClearance, out Vector2 fallback) &&
                Pathfinder.AreStaticallyConnected(fallback, Titania2Center, ConfigData.MinimumClearance))
            {
                Debug.LogError($"Beenoculars found no connected map-edge reinforcement lane; spawning inside the playable arena at {fallback}.");
                startingPosition = fallback;
                nextPosition = fallback;
                return;
            }

            // Titania itself is known to be in the playable mission arena. This is preferable to
            // allowing generic off-map formation placement to choose an isolated compartment.
            Debug.LogError("Beenoculars could not resolve a connected reinforcement lane; spawning at Titania as a final safety fallback.");
            startingPosition = Titania2Center;
            nextPosition = Titania2Center;
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

            // Keep enough physical room around the entry that generic formation placement does
            // not have to search into a neighboring compartment just to fit the squad.
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
