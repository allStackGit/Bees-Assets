using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Immutable tactical setup attached to a curated player-derived adversarial scenario. The first
/// reconstructable-state layer deliberately controls only map size and initial center-to-center
/// engagement distance. Global spawn angle and ship facing remain randomized as augmentation until
/// richer state/script replay is introduced.
/// </summary>
internal sealed class RlPlayerDerivedTacticalGeometry
{
    internal const string CatalogFlag = "--bees-adversarial-geometry-catalog";
    internal const string FixedGeometryFlag = "--bees-rl-fixed-geometry";
    internal const float MinimumMapSize = 10f;
    internal const float MaximumSpawnSeparationRatio = 0.75f;
    internal const int MaximumScenarioCount = 64;

    private const string PlayerDerivedTagPrefix = "player-derived:";

    private static Dictionary<string, RlPlayerDerivedTacticalGeometry> _catalog;
    private static RlPlayerDerivedTacticalGeometry _fixedGeometry;
    private static bool _parsed;

    internal readonly float MapSize;
    internal readonly float SpawnSeparationRatio;

    internal RlPlayerDerivedTacticalGeometry(float mapSize, float spawnSeparationRatio)
    {
        Validate(mapSize, spawnSeparationRatio);
        MapSize = mapSize;
        SpawnSeparationRatio = spawnSeparationRatio;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        _catalog = null;
        _fixedGeometry = null;
        _parsed = false;
    }

    internal static bool TryGetCurrent(Level level, out RlPlayerDerivedTacticalGeometry geometry)
    {
        geometry = null;
        if (level == null)
        {
            return false;
        }

        EnsureParsed(Environment.GetCommandLineArgs());
        string pressureTag = RlOneVsOnePerArenaMatchups.GetCurrentPressureTag(level);
        if (!string.IsNullOrEmpty(pressureTag) &&
            pressureTag.StartsWith(PlayerDerivedTagPrefix, StringComparison.Ordinal))
        {
            string scenarioId = pressureTag.Substring(PlayerDerivedTagPrefix.Length);
            if (_catalog.TryGetValue(scenarioId, out geometry))
            {
                return true;
            }
        }

        if (_fixedGeometry != null)
        {
            geometry = _fixedGeometry;
            return true;
        }
        return false;
    }

    internal static IReadOnlyDictionary<string, RlPlayerDerivedTacticalGeometry> ParseCatalogForTests(string[] args)
    {
        return ParseCatalog(ReadFlagValue(args, CatalogFlag));
    }

    internal static RlPlayerDerivedTacticalGeometry ParseFixedForTests(string[] args)
    {
        string value = ReadFlagValue(args, FixedGeometryFlag);
        RlPlayerDerivedTacticalGeometry geometry =
            value == null ? null : ParseGeometry(value, FixedGeometryFlag);
        ValidateFixedGeometryMode(args, geometry);
        return geometry;
    }

    internal static void ResetForTests()
    {
        ResetForSceneLoad();
    }

    private static void EnsureParsed(string[] args)
    {
        if (_parsed)
        {
            return;
        }

        string catalogValue = ReadFlagValue(args, CatalogFlag);
        string fixedValue = ReadFlagValue(args, FixedGeometryFlag);
        Dictionary<string, RlPlayerDerivedTacticalGeometry> catalog = ParseCatalog(catalogValue);
        RlPlayerDerivedTacticalGeometry fixedGeometry =
            fixedValue == null ? null : ParseGeometry(fixedValue, FixedGeometryFlag);
        if (catalog.Count > 0 && fixedGeometry != null)
        {
            throw new ArgumentException(
                $"{CatalogFlag} cannot be combined with {FixedGeometryFlag}.");
        }
        ValidateFixedGeometryMode(args, fixedGeometry);

        _catalog = catalog;
        _fixedGeometry = fixedGeometry;
        _parsed = true;
    }

    private static void ValidateFixedGeometryMode(
        string[] args,
        RlPlayerDerivedTacticalGeometry fixedGeometry)
    {
        if (fixedGeometry != null && !RlOneVsOneEvaluationSideChannel.IsEvaluationMode(args))
        {
            throw new ArgumentException(
                $"{FixedGeometryFlag} is reserved for authoritative evaluator runs. " +
                $"Training pressure must use the registry-derived {CatalogFlag} instead.");
        }
    }

    private static Dictionary<string, RlPlayerDerivedTacticalGeometry> ParseCatalog(string encoded)
    {
        Dictionary<string, RlPlayerDerivedTacticalGeometry> result =
            new Dictionary<string, RlPlayerDerivedTacticalGeometry>(StringComparer.Ordinal);
        if (encoded == null)
        {
            return result;
        }

        string[] entries = encoded.Split(';');
        if (entries.Length == 0 || entries.Length > MaximumScenarioCount)
        {
            throw new ArgumentException(
                $"{CatalogFlag} requires between 1 and {MaximumScenarioCount} geometry entries.");
        }
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            int separator = entry.IndexOf(':');
            if (separator <= 0 || separator >= entry.Length - 1)
            {
                throw new ArgumentException(
                    $"{CatalogFlag} entry '{entry}' must use adv-<id>:mapSize,separationRatio.");
            }

            string scenarioId = entry.Substring(0, separator).Trim();
            ValidateScenarioId(scenarioId);
            if (result.ContainsKey(scenarioId))
            {
                throw new ArgumentException($"{CatalogFlag} contains duplicate scenario {scenarioId}.");
            }
            result.Add(
                scenarioId,
                ParseGeometry(entry.Substring(separator + 1), $"{CatalogFlag} scenario {scenarioId}"));
        }
        return result;
    }

    private static RlPlayerDerivedTacticalGeometry ParseGeometry(string value, string label)
    {
        string[] values = (value ?? string.Empty).Split(',');
        if (values.Length != 2)
        {
            throw new ArgumentException($"{label} must use mapSize,separationRatio.");
        }

        float mapSize;
        float separationRatio;
        if (!float.TryParse(values[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mapSize) ||
            !float.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out separationRatio))
        {
            throw new ArgumentException($"{label} contains a non-numeric geometry value.");
        }
        Validate(mapSize, separationRatio);
        return new RlPlayerDerivedTacticalGeometry(mapSize, separationRatio);
    }

    private static void Validate(float mapSize, float spawnSeparationRatio)
    {
        if (float.IsNaN(mapSize) || float.IsInfinity(mapSize) || mapSize < MinimumMapSize)
        {
            throw new ArgumentException(
                $"Tactical map size must be at least {MinimumMapSize.ToString("0", CultureInfo.InvariantCulture)}.");
        }
        if (float.IsNaN(spawnSeparationRatio) || float.IsInfinity(spawnSeparationRatio) ||
            spawnSeparationRatio <= 0f || spawnSeparationRatio > MaximumSpawnSeparationRatio)
        {
            throw new ArgumentException(
                "Tactical spawn separation ratio must be greater than 0 and no greater than " +
                MaximumSpawnSeparationRatio.ToString("0.##", CultureInfo.InvariantCulture) + ".");
        }
    }

    private static string ReadFlagValue(string[] args, string flag)
    {
        if (args == null)
        {
            return null;
        }

        string value = null;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument == null)
            {
                continue;
            }

            string candidate = null;
            if (argument.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                {
                    throw new ArgumentException($"{flag} requires a value.");
                }
                candidate = args[++i];
            }
            else
            {
                string prefix = flag + "=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = argument.Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        throw new ArgumentException($"{flag} requires a value.");
                    }
                }
            }

            if (candidate == null)
            {
                continue;
            }
            if (value != null)
            {
                throw new ArgumentException($"{flag} may be specified only once.");
            }
            value = candidate;
        }
        return value;
    }

    private static void ValidateScenarioId(string value)
    {
        if (value == null || value.Length != 28 || !value.StartsWith("adv-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{CatalogFlag} scenario ID '{value}' is invalid.");
        }
        for (int i = 4; i < value.Length; i++)
        {
            char character = value[i];
            bool hex = (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f');
            if (!hex)
            {
                throw new ArgumentException($"{CatalogFlag} scenario ID '{value}' is invalid.");
            }
        }
    }
}
