using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns randomized training-map size per Level. Multi-arena Levels reset independently, so a map
/// size sampled for one episode must never replace the size observed by another still-running arena.
/// Each Level also owns its private RNG stream so asynchronous episode completion cannot perturb the
/// scenario sequence of a peer arena.
/// </summary>
internal static class RlOneVsOneArenaMapSizeState
{
    private const float AuthoredMapSize = 512f;
    private const float BorderThickness = 24f;
    private const float BorderHalfThickness = BorderThickness / 2f;
    private const float BorderOverhang = BorderThickness * 2f;

    private static readonly Dictionary<Level, float> EpisodeMapSizes = new Dictionary<Level, float>();
    private static readonly Dictionary<Level, System.Random> MapSizeRandoms =
        new Dictionary<Level, System.Random>();
    private static RlOneVsOneTrainingOptions _options;

    static RlOneVsOneArenaMapSizeState()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        EpisodeMapSizes.Clear();
        MapSizeRandoms.Clear();
        _options = null;
    }

    private static RlOneVsOneTrainingOptions Options
    {
        get
        {
            if (_options == null)
            {
                _options = RlOneVsOneTrainingOptions.Parse(Environment.GetCommandLineArgs());
            }
            return _options;
        }
    }

    internal static void ConfigureTrainingMap(Level level, Map map)
    {
        if (level == null || map == null || map.SpriteRenderer == null)
        {
            return;
        }

        if (!EpisodeMapSizes.TryGetValue(level, out float mapSize))
        {
            RlOneVsOneTrainingOptions options = Options;
            mapSize = options.HasMapSizeRange
                ? SampleMapSize(level, options.MapSizeMinimum, options.MapSizeMaximum)
                : options.MapSize;
            EpisodeMapSizes[level] = mapSize;
        }

        ApplyMapSize(map, mapSize);
    }

    internal static float GetMapSize(Level level)
    {
        if (level != null && EpisodeMapSizes.TryGetValue(level, out float mapSize))
        {
            return mapSize;
        }

        if (level != null && level.Map != null && level.Map.SpriteRenderer != null)
        {
            return Mathf.Max(level.Map.SpriteRenderer.size.x, level.Map.SpriteRenderer.size.y);
        }

        return RlOneVsOneTrainingBootstrap.CurrentMapSize;
    }

    internal static float GetSpawnRadius(Level level)
    {
        return GetMapSize(level) / 4f;
    }

    internal static Vector2 GetShipFormationOffset(Level level, int shipIndex)
    {
        int shipCount = RlOneVsOneTrainingBootstrap.CurrentShipsPerSide;
        if (shipIndex < 0 || shipIndex >= shipCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }
        if (shipCount == 1)
        {
            return Vector2.zero;
        }

        int columns = Mathf.CeilToInt(Mathf.Sqrt(shipCount));
        int rows = Mathf.CeilToInt((float)shipCount / columns);
        int row = shipIndex / columns;
        int column = shipIndex % columns;
        int itemsInRow = Mathf.Min(columns, shipCount - row * columns);
        float spacing = Mathf.Clamp(GetMapSize(level) / (Mathf.Max(columns, rows) + 4f), 1.5f, 6f);
        float x = (column - (itemsInRow - 1) * 0.5f) * spacing;
        float y = (row - (rows - 1) * 0.5f) * spacing;
        return new Vector2(x, y);
    }

    internal static float SampleMapSize(Level level, float minimum, float maximum)
    {
        if (maximum <= minimum)
        {
            return minimum;
        }
        double unit = GetMapSizeRandom(level).NextDouble();
        return minimum + (float)(unit * (maximum - minimum));
    }

    private static System.Random GetMapSizeRandom(Level level)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        if (!MapSizeRandoms.TryGetValue(level, out System.Random random))
        {
            random = new System.Random(RlOneVsOneScenarioSeed.Create());
            MapSizeRandoms.Add(level, random);
        }
        return random;
    }

    private static void ApplyMapSize(Map map, float mapSize)
    {
        float spawnRadius = mapSize / 4f;
        map.SpriteRenderer.size = new Vector2(mapSize, mapSize);
        float scale = mapSize / AuthoredMapSize;
        map.SizeMultiplier = new Vector2(scale, scale);
        map.UserStartingPosition = new Vector2(0f, -spawnRadius);
        map.AIStartingPosition = new Vector2(0f, spawnRadius);

        float halfMap = mapSize / 2f;
        float borderCenter = halfMap + BorderHalfThickness;
        float longBorder = mapSize + BorderOverhang;
        MapBorder[] borders = map.GetComponentsInChildren<MapBorder>(true);
        for (int i = 0; i < borders.Length; i++)
        {
            Transform border = borders[i].transform;
            string borderName = border.name;
            Vector3 position = border.localPosition;
            Vector3 size = border.localScale;

            if (borderName.Contains("Top Border"))
            {
                position.x = 0f;
                position.y = borderCenter;
                size.x = longBorder;
                size.y = BorderThickness;
            }
            else if (borderName.Contains("Bottom Border"))
            {
                position.x = 0f;
                position.y = -borderCenter;
                size.x = longBorder;
                size.y = BorderThickness;
            }
            else if (borderName.Contains("Right Border"))
            {
                position.x = borderCenter;
                position.y = 0f;
                size.x = BorderThickness;
                size.y = longBorder;
            }
            else if (borderName.Contains("Left Border"))
            {
                position.x = -borderCenter;
                position.y = 0f;
                size.x = BorderThickness;
                size.y = longBorder;
            }
            else
            {
                continue;
            }

            border.localPosition = position;
            border.localScale = size;
        }
    }

    private static void HandleEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (level != null)
        {
            // Keep the Level's RNG stream alive across episode resets so its sequence is independent
            // from when other arenas finish. Only the sampled value belongs to the completed episode.
            EpisodeMapSizes.Remove(level);
        }
    }

    internal static int GetTrackedLevelCountForTests()
    {
        return EpisodeMapSizes.Count;
    }

    internal static void SetMapSizeForTests(Level level, float mapSize)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }
        EpisodeMapSizes[level] = mapSize;
    }

    internal static void SetRandomSeedForTests(Level level, int seed)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }
        MapSizeRandoms[level] = new System.Random(seed);
    }

    internal static void ResetForTests()
    {
        EpisodeMapSizes.Clear();
        MapSizeRandoms.Clear();
        _options = null;
    }
}
