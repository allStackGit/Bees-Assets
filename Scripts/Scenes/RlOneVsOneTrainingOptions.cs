using Assets.Scripts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

internal enum RlOneVsOneMatchupMode
{
    Fixed,
    Sampled,
}

/// <summary>
/// Immutable command-line configuration for the dedicated ML-Agents combat scene. ML-Agents passes
/// these arguments to standalone Unity workers through --env-args, keeping curriculum/environment
/// changes out of serialized scene state and out of the trainer YAML.
/// </summary>
internal sealed class RlOneVsOneTrainingOptions
{
    internal const string HealthRatioFlag = "--rl-health-ratio";
    internal const string MapSizeFlag = "--rl-map-size";
    internal const string MapSizeMinimumFlag = "--rl-map-size-min";
    internal const string MapSizeMaximumFlag = "--rl-map-size-max";
    internal const string EpisodeTimeoutFlag = "--rl-episode-timeout";
    internal const string ShipsPerSideFlag = "--rl-ships-per-side";
    internal const string DecisionPeriodFlag = "--rl-decision-period";
    internal const string BeeShipTypesFlag = "--rl-bee-ship-types";
    internal const string HumanShipTypesFlag = "--rl-human-ship-types";
    internal const string MatchupModeFlag = "--rl-matchup-mode";

    internal const float DefaultHealthRatio = 0.25f;
    internal const float DefaultMapSize = 30f;
    internal const int DefaultEpisodeTimeoutSeconds = 120;
    internal const int DefaultShipsPerSide = 1;
    internal const int DefaultDecisionPeriod = 5;
    internal const int MaximumShipsPerSide = 16;
    internal const float MinimumMapSize = 10f;
    internal const RlOneVsOneMatchupMode DefaultMatchupMode = RlOneVsOneMatchupMode.Fixed;

    private static readonly ConfigData.ShipTypes[] DefaultSampledBeeShipTypes =
    {
        ConfigData.ShipTypes.Beehive,
        ConfigData.ShipTypes.Bumblebee,
        ConfigData.ShipTypes.CarpenterBee,
        ConfigData.ShipTypes.Honeybee,
        ConfigData.ShipTypes.Hornet,
        ConfigData.ShipTypes.Leafcutter,
        ConfigData.ShipTypes.Queen,
        ConfigData.ShipTypes.Wasp,
        ConfigData.ShipTypes.YellowJacket,
    };

    private static readonly ConfigData.ShipTypes[] DefaultSampledHumanShipTypes =
    {
        ConfigData.ShipTypes.Barge,
        ConfigData.ShipTypes.Carrier,
        ConfigData.ShipTypes.Cruiser,
        ConfigData.ShipTypes.Dreadnought,
        ConfigData.ShipTypes.Factory,
        ConfigData.ShipTypes.FireBarge,
        ConfigData.ShipTypes.Flagship,
        ConfigData.ShipTypes.Frigate,
        ConfigData.ShipTypes.Gunship,
        ConfigData.ShipTypes.Scout,
        ConfigData.ShipTypes.WarpGate,
    };

    private static readonly Random MapSizeRandom = new Random(Guid.NewGuid().GetHashCode());
    private static bool _mapSizeEpisodeSubscriptionInstalled;
    private static bool _sampledMapSizeValid;
    private static float _sampledMapSize;
    private static float _sampledMapSizeMinimum;
    private static float _sampledMapSizeMaximum;

    private readonly List<ConfigData.ShipTypes> _beeShipTypes;
    private readonly List<ConfigData.ShipTypes> _humanShipTypes;
    private bool _beeShipTypesSpecified;
    private bool _humanShipTypesSpecified;
    private bool _mapSizeSpecified;
    private bool _mapSizeMinimumSpecified;
    private bool _mapSizeMaximumSpecified;
    private float _mapSize;
    private float _mapSizeMinimum;
    private float _mapSizeMaximum;

    internal float HealthRatio { get; private set; }
    internal float MapSize => GetCurrentMapSize();
    internal float MapSizeMinimum => HasMapSizeRange ? _mapSizeMinimum : _mapSize;
    internal float MapSizeMaximum => HasMapSizeRange ? _mapSizeMaximum : _mapSize;
    internal bool HasMapSizeRange => _mapSizeMinimumSpecified && _mapSizeMaximumSpecified;
    internal int EpisodeTimeoutSeconds { get; private set; }
    internal int ShipsPerSide { get; private set; }
    internal int DecisionPeriod { get; private set; }
    internal RlOneVsOneMatchupMode MatchupMode { get; private set; }
    internal IReadOnlyList<ConfigData.ShipTypes> BeeShipTypes => _beeShipTypes;
    internal IReadOnlyList<ConfigData.ShipTypes> HumanShipTypes => _humanShipTypes;

    private RlOneVsOneTrainingOptions()
    {
        HealthRatio = DefaultHealthRatio;
        _mapSize = DefaultMapSize;
        _mapSizeMinimum = DefaultMapSize;
        _mapSizeMaximum = DefaultMapSize;
        EpisodeTimeoutSeconds = DefaultEpisodeTimeoutSeconds;
        ShipsPerSide = DefaultShipsPerSide;
        DecisionPeriod = DefaultDecisionPeriod;
        MatchupMode = DefaultMatchupMode;
        _beeShipTypes = new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Wasp };
        _humanShipTypes = new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Gunship };
    }

    internal static RlOneVsOneTrainingOptions Parse(string[] args)
    {
        RlOneVsOneTrainingOptions options = new RlOneVsOneTrainingOptions();
        if (args == null)
        {
            return options;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            string value;
            if (TryReadOption(argument, HealthRatioFlag, args, ref i, out value))
            {
                options.HealthRatio = ParseFloat(value, HealthRatioFlag);
            }
            else if (TryReadOption(argument, MapSizeFlag, args, ref i, out value))
            {
                options._mapSize = ParseFloat(value, MapSizeFlag);
                options._mapSizeSpecified = true;
            }
            else if (TryReadOption(argument, MapSizeMinimumFlag, args, ref i, out value))
            {
                options._mapSizeMinimum = ParseFloat(value, MapSizeMinimumFlag);
                options._mapSizeMinimumSpecified = true;
            }
            else if (TryReadOption(argument, MapSizeMaximumFlag, args, ref i, out value))
            {
                options._mapSizeMaximum = ParseFloat(value, MapSizeMaximumFlag);
                options._mapSizeMaximumSpecified = true;
            }
            else if (TryReadOption(argument, EpisodeTimeoutFlag, args, ref i, out value))
            {
                options.EpisodeTimeoutSeconds = ParseInt(value, EpisodeTimeoutFlag);
            }
            else if (TryReadOption(argument, ShipsPerSideFlag, args, ref i, out value))
            {
                options.ShipsPerSide = ParseInt(value, ShipsPerSideFlag);
            }
            else if (TryReadOption(argument, DecisionPeriodFlag, args, ref i, out value))
            {
                options.DecisionPeriod = ParseInt(value, DecisionPeriodFlag);
            }
            else if (TryReadOption(argument, BeeShipTypesFlag, args, ref i, out value))
            {
                ReplaceShipTypes(options._beeShipTypes, value, BeeShipTypesFlag);
                options._beeShipTypesSpecified = true;
            }
            else if (TryReadOption(argument, HumanShipTypesFlag, args, ref i, out value))
            {
                ReplaceShipTypes(options._humanShipTypes, value, HumanShipTypesFlag);
                options._humanShipTypesSpecified = true;
            }
            else if (TryReadOption(argument, MatchupModeFlag, args, ref i, out value))
            {
                options.MatchupMode = ParseMatchupMode(value);
            }
            else if (argument.StartsWith("--rl-", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown RL training option '{argument}'.");
            }
        }

        options.ApplySampledRosterDefaults();
        options.Validate();
        return options;
    }

    internal ConfigData.ShipTypes GetBeeShipType(int shipIndex)
    {
        return GetShipType(_beeShipTypes, shipIndex);
    }

    internal ConfigData.ShipTypes GetHumanShipType(int shipIndex)
    {
        return GetShipType(_humanShipTypes, shipIndex);
    }

    internal string Describe()
    {
        string mapDescription = HasMapSizeRange
            ? $"map_size_range={FormatFloat(_mapSizeMinimum)}..{FormatFloat(_mapSizeMaximum)} sampled_map_size={FormatFloat(MapSize)}"
            : $"map_size={FormatFloat(_mapSize)}";
        return $"health_ratio={FormatFloat(HealthRatio)} {mapDescription} " +
               $"episode_timeout={EpisodeTimeoutSeconds}s ships_per_side={ShipsPerSide} " +
               $"decision_period={DecisionPeriod} matchup_mode={MatchupMode.ToString().ToLowerInvariant()} " +
               $"bee_ship_types={JoinShipTypes(_beeShipTypes)} human_ship_types={JoinShipTypes(_humanShipTypes)}";
    }

    private float GetCurrentMapSize()
    {
        if (!HasMapSizeRange)
        {
            return _mapSize;
        }

        EnsureMapSizeEpisodeSubscription();
        if (!_sampledMapSizeValid ||
            _sampledMapSizeMinimum != _mapSizeMinimum ||
            _sampledMapSizeMaximum != _mapSizeMaximum)
        {
            double unit = MapSizeRandom.NextDouble();
            _sampledMapSize = _mapSizeMinimum + (float)(unit * (_mapSizeMaximum - _mapSizeMinimum));
            _sampledMapSizeMinimum = _mapSizeMinimum;
            _sampledMapSizeMaximum = _mapSizeMaximum;
            _sampledMapSizeValid = true;
        }
        return _sampledMapSize;
    }

    private static void EnsureMapSizeEpisodeSubscription()
    {
        if (_mapSizeEpisodeSubscriptionInstalled)
        {
            return;
        }
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
        _mapSizeEpisodeSubscriptionInstalled = true;
    }

    private static void HandleEpisodeEnded(Assets.Scripts.Levels.Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        _sampledMapSizeValid = false;
    }

    private void ApplySampledRosterDefaults()
    {
        if (MatchupMode != RlOneVsOneMatchupMode.Sampled)
        {
            return;
        }
        if (!_beeShipTypesSpecified)
        {
            ReplaceShipTypes(_beeShipTypes, DefaultSampledBeeShipTypes);
        }
        if (!_humanShipTypesSpecified)
        {
            ReplaceShipTypes(_humanShipTypes, DefaultSampledHumanShipTypes);
        }
    }

    private void Validate()
    {
        if (float.IsNaN(HealthRatio) || float.IsInfinity(HealthRatio) || HealthRatio <= 0f || HealthRatio > 1f)
        {
            throw new ArgumentException($"{HealthRatioFlag} must be greater than 0 and no greater than 1.");
        }

        ValidateMapSizeOptions();

        if (EpisodeTimeoutSeconds <= 0)
        {
            throw new ArgumentException($"{EpisodeTimeoutFlag} must be a positive whole number of seconds.");
        }
        if (ShipsPerSide < 1 || ShipsPerSide > MaximumShipsPerSide)
        {
            throw new ArgumentException($"{ShipsPerSideFlag} must be between 1 and {MaximumShipsPerSide}.");
        }
        if (DecisionPeriod <= 0)
        {
            throw new ArgumentException($"{DecisionPeriodFlag} must be a positive whole number of physics steps.");
        }

        if (MatchupMode == RlOneVsOneMatchupMode.Fixed)
        {
            ValidateComposition(_beeShipTypes, BeeShipTypesFlag);
            ValidateComposition(_humanShipTypes, HumanShipTypesFlag);
            return;
        }

        if (ConfigData.Configuration == null || !ConfigData.Configuration.IsLoaded)
        {
            throw new InvalidOperationException("Sampled RL matchup validation requires loaded configuration settings.");
        }

        ValidateSampledCandidatePool(_beeShipTypes, ConfigData.Configuration.BeeSide, BeeShipTypesFlag);
        ValidateSampledCandidatePool(_humanShipTypes, ConfigData.Configuration.HumanSide, HumanShipTypesFlag);
    }

    private void ValidateMapSizeOptions()
    {
        if (_mapSizeMinimumSpecified != _mapSizeMaximumSpecified)
        {
            throw new ArgumentException($"{MapSizeMinimumFlag} and {MapSizeMaximumFlag} must be specified together.");
        }
        if (_mapSizeSpecified && _mapSizeMinimumSpecified)
        {
            throw new ArgumentException($"{MapSizeFlag} cannot be combined with {MapSizeMinimumFlag}/{MapSizeMaximumFlag}.");
        }

        if (!HasMapSizeRange)
        {
            ValidateMapSize(_mapSize, MapSizeFlag);
            _mapSizeMinimum = _mapSize;
            _mapSizeMaximum = _mapSize;
            return;
        }

        ValidateMapSize(_mapSizeMinimum, MapSizeMinimumFlag);
        ValidateMapSize(_mapSizeMaximum, MapSizeMaximumFlag);
        if (_mapSizeMaximum < _mapSizeMinimum)
        {
            throw new ArgumentException($"{MapSizeMaximumFlag} must be greater than or equal to {MapSizeMinimumFlag}.");
        }
    }

    private static void ValidateMapSize(float value, string flag)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < MinimumMapSize)
        {
            throw new ArgumentException($"{flag} must be at least {MinimumMapSize.ToString("0", CultureInfo.InvariantCulture)}.");
        }
    }

    private void ValidateComposition(List<ConfigData.ShipTypes> shipTypes, string flag)
    {
        if (shipTypes.Count != 1 && shipTypes.Count != ShipsPerSide)
        {
            throw new ArgumentException(
                $"{flag} must contain either one type (repeated for every ship) or exactly {ShipsPerSide} comma-separated types.");
        }
    }

    private static void ValidateSampledCandidatePool(List<ConfigData.ShipTypes> shipTypes, int expectedSide, string flag)
    {
        if (shipTypes.Count == 0)
        {
            throw new ArgumentException($"{flag} requires at least one ship type in sampled mode.");
        }

        bool hasArmedCandidate = false;
        HashSet<ConfigData.ShipTypes> seen = new HashSet<ConfigData.ShipTypes>();
        for (int i = 0; i < shipTypes.Count; i++)
        {
            ConfigData.ShipTypes shipType = shipTypes[i];
            int actualSide;
            if (!Utilities.ConvertShipTypeToSide.TryGetValue(shipType, out actualSide) || actualSide != expectedSide)
            {
                throw new ArgumentException($"{flag} contains {shipType}, which does not belong to side {expectedSide}.");
            }
            if (!seen.Add(shipType))
            {
                throw new ArgumentException($"{flag} contains duplicate sampled candidate {shipType}.");
            }
            if (!RlShipCombatCapability.IsWeaponless(shipType))
            {
                hasArmedCandidate = true;
            }
        }

        if (!hasArmedCandidate)
        {
            throw new ArgumentException($"{flag} must contain at least one armed ship type in sampled mode.");
        }
    }

    private static ConfigData.ShipTypes GetShipType(List<ConfigData.ShipTypes> shipTypes, int shipIndex)
    {
        if (shipIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }
        if (shipTypes.Count == 1)
        {
            return shipTypes[0];
        }
        if (shipIndex >= shipTypes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }
        return shipTypes[shipIndex];
    }

    private static bool TryReadOption(string argument, string optionName, string[] args, ref int index, out string value)
    {
        if (argument.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--"))
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }
            index++;
            value = args[index];
            return true;
        }

        string prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{optionName} requires a value.");
            }
            return true;
        }

        value = null;
        return false;
    }

    private static float ParseFloat(string value, string flag)
    {
        float parsed;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            throw new ArgumentException($"{flag} value '{value}' is not a valid number.");
        }
        return parsed;
    }

    private static int ParseInt(string value, string flag)
    {
        int parsed;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            throw new ArgumentException($"{flag} value '{value}' is not a valid whole number.");
        }
        return parsed;
    }

    private static RlOneVsOneMatchupMode ParseMatchupMode(string value)
    {
        RlOneVsOneMatchupMode parsed;
        if (!Enum.TryParse(value, true, out parsed))
        {
            throw new ArgumentException($"{MatchupModeFlag} value '{value}' must be 'fixed' or 'sampled'.");
        }
        return parsed;
    }

    private static void ReplaceShipTypes(List<ConfigData.ShipTypes> destination, string value, string flag)
    {
        destination.Clear();
        string[] tokens = value.Split(',');
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i].Trim();
            if (token.Length == 0)
            {
                throw new ArgumentException($"{flag} contains an empty ship type.");
            }

            ConfigData.ShipTypes parsed;
            if (!TryParseShipType(token, out parsed))
            {
                throw new ArgumentException($"{flag} contains unknown ship type '{token}'.");
            }
            destination.Add(parsed);
        }
        if (destination.Count == 0)
        {
            throw new ArgumentException($"{flag} requires at least one ship type.");
        }
    }

    private static void ReplaceShipTypes(List<ConfigData.ShipTypes> destination, IReadOnlyList<ConfigData.ShipTypes> source)
    {
        destination.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            destination.Add(source[i]);
        }
    }

    private static bool TryParseShipType(string value, out ConfigData.ShipTypes shipType)
    {
        string normalized = NormalizeShipTypeName(value);
        string[] names = Enum.GetNames(typeof(ConfigData.ShipTypes));
        for (int i = 0; i < names.Length; i++)
        {
            if (!NormalizeShipTypeName(names[i]).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            shipType = (ConfigData.ShipTypes)Enum.Parse(typeof(ConfigData.ShipTypes), names[i]);
            return true;
        }
        shipType = default(ConfigData.ShipTypes);
        return false;
    }

    private static string NormalizeShipTypeName(string value)
    {
        return value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
    }

    private static string JoinShipTypes(List<ConfigData.ShipTypes> shipTypes)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < shipTypes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(shipTypes[i]);
        }
        return builder.ToString();
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}