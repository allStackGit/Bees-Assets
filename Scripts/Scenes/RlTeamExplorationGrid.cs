using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-episode, per-side memory of which global map regions have recently been covered by allied
/// sight. The policy receives one 16x16 global grid; all allied ships contribute to the same grid.
/// </summary>
internal sealed class RlTeamExplorationGrid
{
    internal const int Size = 16;
    internal const int CellCount = Size * Size;

    private readonly float[] _lastSeenProgress = new float[CellCount];

    internal RlTeamExplorationGrid()
    {
        Reset();
    }

    internal void Reset()
    {
        for (int i = 0; i < _lastSeenProgress.Length; i++)
        {
            _lastSeenProgress[i] = -1f;
        }
    }

    internal void Update(Level level, List<Ship> ships, float episodeProgress)
    {
        if (level == null || ships == null || level.MaxX <= level.MinX || level.MaxY <= level.MinY)
        {
            return;
        }

        float width = level.MaxX - level.MinX;
        float height = level.MaxY - level.MinY;
        float cellWidth = width / Size;
        float cellHeight = height / Size;

        for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
        {
            Ship ship = ships[shipIndex];
            int visionRange = HiveMindVision.GetEffectiveRange(ship);
            if (ship == null || ship.IsDead || visionRange <= 0)
            {
                continue;
            }

            Vector2 position = ship.GetPosition();
            float sightSquared = visionRange * visionRange;
            int minX = Mathf.Clamp(Mathf.FloorToInt((position.x - visionRange - level.MinX) / cellWidth), 0, Size - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt((position.x + visionRange - level.MinX) / cellWidth), 0, Size - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt((position.y - visionRange - level.MinY) / cellHeight), 0, Size - 1);
            int maxY = Mathf.Clamp(Mathf.FloorToInt((position.y + visionRange - level.MinY) / cellHeight), 0, Size - 1);

            for (int y = minY; y <= maxY; y++)
            {
                float centerY = level.MinY + (y + 0.5f) * cellHeight;
                for (int x = minX; x <= maxX; x++)
                {
                    float centerX = level.MinX + (x + 0.5f) * cellWidth;
                    float dx = centerX - position.x;
                    float dy = centerY - position.y;
                    if (dx * dx + dy * dy <= sightSquared)
                    {
                        _lastSeenProgress[y * Size + x] = episodeProgress;
                    }
                }
            }
        }
    }

    internal float GetFreshness(int worldIndex, float episodeProgress)
    {
        if (worldIndex < 0 || worldIndex >= _lastSeenProgress.Length)
        {
            return 0f;
        }

        float lastSeen = _lastSeenProgress[worldIndex];
        if (lastSeen < 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(1f - Mathf.Max(0f, episodeProgress - lastSeen));
    }
}
