using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

internal sealed class RlPlayerDerivedAdversarialScenario
{
    internal readonly string ScenarioId;
    internal readonly ConfigData.ShipTypes[] BeeComposition;
    internal readonly ConfigData.ShipTypes[] HumanComposition;
    internal readonly double TargetFraction;

    internal RlPlayerDerivedAdversarialScenario(
        string scenarioId,
        ConfigData.ShipTypes[] beeComposition,
        ConfigData.ShipTypes[] humanComposition,
        double targetFraction)
    {
        ScenarioId = scenarioId;
        BeeComposition = beeComposition;
        HumanComposition = humanComposition;
        TargetFraction = targetFraction;
    }
}

/// <summary>
/// Parses an operator-curated set of player-derived matchup scenarios. These are not replayed
/// trajectories: they only reserve a bounded fraction of dedicated-training episodes for exact
/// fleet compositions while the current policy still generates fresh PPO experience.
/// </summary>
internal static class RlPlayerDerivedAdversarialPressure
{
    internal const string CommandLineFlag = "--bees-adversarial-matchups";
    internal const int MaximumScenarioCount = 64;
    internal const double MaximumScenarioFraction = 0.5d;
    internal const double MaximumCombinedFraction = 0.5d;

    internal static IReadOnlyList<RlPlayerDerivedAdversarialScenario> Parse(
        string[] args,
        RlOneVsOneTrainingOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        string encoded = ReadEncodedValue(args);
        if (encoded == null)
        {
            return Array.Empty<RlPlayerDerivedAdversarialScenario>();
        }
        if (options.MatchupMode != RlOneVsOneMatchupMode.Sampled)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} requires --rl-matchup-mode=sampled so normal training remains available.");
        }

        string[] entries = encoded.Split(';');
        if (entries.Length == 0 || entries.Length > MaximumScenarioCount)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} requires between 1 and {MaximumScenarioCount} scenarios.");
        }

        List<RlPlayerDerivedAdversarialScenario> scenarios =
            new List<RlPlayerDerivedAdversarialScenario>(entries.Length);
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        double totalFraction = 0d;

        for (int i = 0; i < entries.Length; i++)
        {
            RlPlayerDerivedAdversarialScenario scenario = ParseEntry(entries[i], options);
            if (!ids.Add(scenario.ScenarioId))
            {
                throw new ArgumentException(
                    $"{CommandLineFlag} contains duplicate scenario ID {scenario.ScenarioId}.");
            }
            totalFraction += scenario.TargetFraction;
            scenarios.Add(scenario);
        }

        if (totalFraction > MaximumCombinedFraction + 1e-12d)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} requests {totalFraction.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"of episodes; combined player-derived pressure may not exceed " +
                $"{MaximumCombinedFraction.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }
        return scenarios;
    }

    private static RlPlayerDerivedAdversarialScenario ParseEntry(
        string entry,
        RlOneVsOneTrainingOptions options)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new ArgumentException($"{CommandLineFlag} contains an empty scenario.");
        }

        int colon = entry.IndexOf(':');
        int greater = entry.IndexOf('>', colon + 1);
        int at = entry.LastIndexOf('@');
        if (colon <= 0 || greater <= colon + 1 || at <= greater + 1 || at >= entry.Length - 1)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} scenario '{entry}' must use id:Bee,...>Human,...@fraction.");
        }

        string scenarioId = entry.Substring(0, colon).Trim();
        ValidateScenarioId(scenarioId);
        ConfigData.ShipTypes[] bees = ParseComposition(
            entry.Substring(colon + 1, greater - colon - 1),
            options.BeeShipTypes,
            options.ShipsPerSide,
            ConfigData.Configuration.BeeSide,
            scenarioId,
            "Bee");
        ConfigData.ShipTypes[] humans = ParseComposition(
            entry.Substring(greater + 1, at - greater - 1),
            options.HumanShipTypes,
            options.ShipsPerSide,
            ConfigData.Configuration.HumanSide,
            scenarioId,
            "Human");

        double fraction;
        string fractionText = entry.Substring(at + 1).Trim();
        if (!double.TryParse(fractionText, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction) ||
            double.IsNaN(fraction) || double.IsInfinity(fraction) ||
            fraction <= 0d || fraction > MaximumScenarioFraction)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} scenario {scenarioId} fraction must be greater than 0 and no greater than " +
                MaximumScenarioFraction.ToString("0.###", CultureInfo.InvariantCulture) + ".");
        }

        return new RlPlayerDerivedAdversarialScenario(scenarioId, bees, humans, fraction);
    }

    private static ConfigData.ShipTypes[] ParseComposition(
        string text,
        IReadOnlyList<ConfigData.ShipTypes> candidatePool,
        int shipsPerSide,
        int expectedSide,
        string scenarioId,
        string label)
    {
        string[] tokens = text.Split(',');
        if (tokens.Length != shipsPerSide)
        {
            throw new ArgumentException(
                $"{CommandLineFlag} scenario {scenarioId} {label} composition must contain exactly " +
                $"{shipsPerSide} ships.");
        }

        ConfigData.ShipTypes[] composition = new ConfigData.ShipTypes[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            ConfigData.ShipTypes shipType;
            if (!TryParseShipType(tokens[i].Trim(), out shipType))
            {
                throw new ArgumentException(
                    $"{CommandLineFlag} scenario {scenarioId} contains unknown {label} ship type '{tokens[i]}'.");
            }
            RlOneVsOneMatchupSampler.ValidateSide(shipType, expectedSide, CommandLineFlag);
            if (!Contains(candidatePool, shipType))
            {
                throw new ArgumentException(
                    $"{CommandLineFlag} scenario {scenarioId} uses {shipType}, which is not present in the " +
                    $"configured {label} sampled candidate pool.");
            }
            composition[i] = shipType;
        }

        if (!RlShipCombatCapability.HasAnyWeapon(composition))
        {
            throw new ArgumentException(
                $"{CommandLineFlag} scenario {scenarioId} {label} composition is entirely weaponless.");
        }
        return composition;
    }

    private static string ReadEncodedValue(string[] args)
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
            if (argument.Equals(CommandLineFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                {
                    throw new ArgumentException($"{CommandLineFlag} requires a value.");
                }
                candidate = args[++i];
            }
            else
            {
                string prefix = CommandLineFlag + "=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = argument.Substring(prefix.Length);
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        throw new ArgumentException($"{CommandLineFlag} requires a value.");
                    }
                }
            }

            if (candidate == null)
            {
                continue;
            }
            if (value != null)
            {
                throw new ArgumentException($"{CommandLineFlag} may be specified only once.");
            }
            value = candidate;
        }
        return value;
    }

    private static void ValidateScenarioId(string value)
    {
        if (value.Length != 28 || !value.StartsWith("adv-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{CommandLineFlag} scenario ID '{value}' is invalid.");
        }
        for (int i = 4; i < value.Length; i++)
        {
            char character = value[i];
            bool hex = (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f');
            if (!hex)
            {
                throw new ArgumentException($"{CommandLineFlag} scenario ID '{value}' is invalid.");
            }
        }
    }

    private static bool Contains(
        IReadOnlyList<ConfigData.ShipTypes> values,
        ConfigData.ShipTypes value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseShipType(string value, out ConfigData.ShipTypes shipType)
    {
        string normalized = NormalizeShipTypeName(value);
        string[] names = Enum.GetNames(typeof(ConfigData.ShipTypes));
        for (int i = 0; i < names.Length; i++)
        {
            if (NormalizeShipTypeName(names[i]).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                shipType = (ConfigData.ShipTypes)Enum.Parse(typeof(ConfigData.ShipTypes), names[i]);
                return true;
            }
        }
        shipType = default(ConfigData.ShipTypes);
        return false;
    }

    private static string NormalizeShipTypeName(string value)
    {
        return (value ?? string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty);
    }
}

/// <summary>
/// Reserves the requested fraction of episodes for curated player-derived compositions and delegates
/// every other episode to the existing sampled/adaptive selector unchanged. Outcomes from reserved
/// episodes are intentionally not fed into the adaptive sampler, keeping the two pressure sources
/// independently measurable.
/// </summary>
internal sealed class RlOneVsOneAdversarialMatchupSelector
{
    private const int RandomSeedOffset = 197933;

    private readonly RlOneVsOneEpisodeMatchupSelector _normalSelector;
    private readonly IReadOnlyList<RlPlayerDerivedAdversarialScenario> _scenarios;
    private readonly System.Random _random;
    private ConfigData.ShipTypes[] _currentBeeComposition;
    private ConfigData.ShipTypes[] _currentHumanComposition;
    private bool _usingPlayerDerivedPressure;
    private string _currentPressureTag = "normal";

    internal bool IsPlayerDerivedPressure => _usingPlayerDerivedPressure;
    internal string CurrentPressureTag => _currentPressureTag;

    internal RlOneVsOneAdversarialMatchupSelector(
        RlOneVsOneTrainingOptions options,
        int seed,
        IReadOnlyList<RlPlayerDerivedAdversarialScenario> scenarios)
    {
        _normalSelector = new RlOneVsOneEpisodeMatchupSelector(options, seed);
        _scenarios = scenarios ?? Array.Empty<RlPlayerDerivedAdversarialScenario>();
        _random = new System.Random(unchecked(seed * 31 + RandomSeedOffset));
    }

    internal void PrepareEpisode()
    {
        _usingPlayerDerivedPressure = false;
        _currentPressureTag = "normal";

        if (_scenarios.Count > 0)
        {
            double roll = _random.NextDouble();
            double cumulative = 0d;
            for (int i = 0; i < _scenarios.Count; i++)
            {
                cumulative += _scenarios[i].TargetFraction;
                if (roll < cumulative)
                {
                    ApplyScenario(_scenarios[i]);
                    return;
                }
            }
        }

        _normalSelector.PrepareEpisode();
    }

    internal ConfigData.ShipTypes GetShipType(int side, int shipIndex)
    {
        if (!_usingPlayerDerivedPressure)
        {
            return _normalSelector.GetShipType(side, shipIndex);
        }
        if (shipIndex < 0 || shipIndex >= _currentBeeComposition.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }
        if (side == ConfigData.Configuration.BeeSide)
        {
            return _currentBeeComposition[shipIndex];
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return _currentHumanComposition[shipIndex];
        }
        throw new ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
    }

    internal void RecordEpisodeOutcome(int winningSide, bool timedOut)
    {
        if (!_usingPlayerDerivedPressure)
        {
            _normalSelector.RecordEpisodeOutcome(winningSide, timedOut);
        }
    }

    private void ApplyScenario(RlPlayerDerivedAdversarialScenario scenario)
    {
        _currentBeeComposition = (ConfigData.ShipTypes[])scenario.BeeComposition.Clone();
        _currentHumanComposition = (ConfigData.ShipTypes[])scenario.HumanComposition.Clone();
        Shuffle(_currentBeeComposition);
        Shuffle(_currentHumanComposition);
        _usingPlayerDerivedPressure = true;
        _currentPressureTag = "player-derived:" + scenario.ScenarioId;
    }

    private void Shuffle(ConfigData.ShipTypes[] composition)
    {
        for (int index = composition.Length - 1; index > 0; index--)
        {
            int swapIndex = _random.Next(index + 1);
            ConfigData.ShipTypes temporary = composition[index];
            composition[index] = composition[swapIndex];
            composition[swapIndex] = temporary;
        }
    }
}

/// <summary>
/// Emits one compact measurement per window so long-running training can verify that requested
/// player-derived pressure is actually being sampled without adding per-episode log spam.
/// </summary>
internal static class RlPlayerDerivedPressureTelemetry
{
    private const int ReportingWindowEpisodes = 1000;

    private sealed class ArenaState
    {
        internal int Episodes;
        internal int PlayerDerivedEpisodes;
        internal readonly Dictionary<string, int> ScenarioCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private static readonly Dictionary<Level, ArenaState> States =
        new Dictionary<Level, ArenaState>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        States.Clear();
    }

    internal static void RecordPrepared(Level level, string pressureTag)
    {
        if (level == null)
        {
            return;
        }
        ArenaState state;
        if (!States.TryGetValue(level, out state))
        {
            state = new ArenaState();
            States.Add(level, state);
        }

        state.Episodes++;
        if (!string.IsNullOrEmpty(pressureTag) && pressureTag.StartsWith("player-derived:", StringComparison.Ordinal))
        {
            state.PlayerDerivedEpisodes++;
            int count;
            state.ScenarioCounts.TryGetValue(pressureTag, out count);
            state.ScenarioCounts[pressureTag] = count + 1;
        }

        if (state.Episodes >= ReportingWindowEpisodes)
        {
            Debug.Log(
                $"[Bees RL] player_derived_pressure episodes={state.PlayerDerivedEpisodes}/{state.Episodes} " +
                $"rate={(double)state.PlayerDerivedEpisodes / state.Episodes:0.000} " +
                $"scenarios={FormatScenarioCounts(state.ScenarioCounts)}");
            state.Episodes = 0;
            state.PlayerDerivedEpisodes = 0;
            state.ScenarioCounts.Clear();
        }
    }

    private static string FormatScenarioCounts(Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }
        List<string> keys = new List<string>(counts.Keys);
        keys.Sort(StringComparer.Ordinal);
        List<string> values = new List<string>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            values.Add(keys[i] + ":" + counts[keys[i]].ToString(CultureInfo.InvariantCulture));
        }
        return string.Join("|", values);
    }

    internal static void ResetForTests()
    {
        States.Clear();
    }
}
