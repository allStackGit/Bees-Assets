using Assets.Scripts;
using System;
using System.Collections.Generic;

internal struct RlOneVsOneMatchup
{
    internal ConfigData.ShipTypes BeeShipType;
    internal ConfigData.ShipTypes HumanShipType;

    internal RlOneVsOneMatchup(ConfigData.ShipTypes beeShipType, ConfigData.ShipTypes humanShipType)
    {
        BeeShipType = beeShipType;
        HumanShipType = humanShipType;
    }
}

/// <summary>
/// Produces a shuffled Cartesian cycle of Bee x Human matchups. Every pair is emitted exactly once
/// before the cycle is reshuffled and repeated, preventing random sampling from starving rare pairs.
/// </summary>
internal sealed class RlOneVsOneMatchupSampler
{
    private readonly List<RlOneVsOneMatchup> _cycle = new List<RlOneVsOneMatchup>();
    private readonly Random _random;
    private int _nextIndex;

    internal RlOneVsOneMatchupSampler(
        IReadOnlyList<ConfigData.ShipTypes> beeShipTypes,
        IReadOnlyList<ConfigData.ShipTypes> humanShipTypes)
        : this(beeShipTypes, humanShipTypes, Guid.NewGuid().GetHashCode())
    {
    }

    internal RlOneVsOneMatchupSampler(
        IReadOnlyList<ConfigData.ShipTypes> beeShipTypes,
        IReadOnlyList<ConfigData.ShipTypes> humanShipTypes,
        int seed)
    {
        if (beeShipTypes == null || beeShipTypes.Count == 0)
        {
            throw new ArgumentException("At least one Bee ship type is required.", nameof(beeShipTypes));
        }
        if (humanShipTypes == null || humanShipTypes.Count == 0)
        {
            throw new ArgumentException("At least one Human ship type is required.", nameof(humanShipTypes));
        }

        for (int beeIndex = 0; beeIndex < beeShipTypes.Count; beeIndex++)
        {
            ConfigData.ShipTypes beeShipType = beeShipTypes[beeIndex];
            ValidateSide(beeShipType, ConfigData.Configuration.BeeSide, nameof(beeShipTypes));
            for (int humanIndex = 0; humanIndex < humanShipTypes.Count; humanIndex++)
            {
                ConfigData.ShipTypes humanShipType = humanShipTypes[humanIndex];
                ValidateSide(humanShipType, ConfigData.Configuration.HumanSide, nameof(humanShipTypes));
                _cycle.Add(new RlOneVsOneMatchup(beeShipType, humanShipType));
            }
        }

        _random = new Random(seed);
        ShuffleCycle();
    }

    internal RlOneVsOneMatchup Next()
    {
        if (_nextIndex >= _cycle.Count)
        {
            ShuffleCycle();
        }

        return _cycle[_nextIndex++];
    }

    private void ShuffleCycle()
    {
        for (int index = _cycle.Count - 1; index > 0; index--)
        {
            int swapIndex = _random.Next(index + 1);
            RlOneVsOneMatchup temporary = _cycle[index];
            _cycle[index] = _cycle[swapIndex];
            _cycle[swapIndex] = temporary;
        }
        _nextIndex = 0;
    }

    internal static void ValidateSide(ConfigData.ShipTypes shipType, int expectedSide, string parameterName)
    {
        int side;
        if (!Utilities.ConvertShipTypeToSide.TryGetValue(shipType, out side) || side != expectedSide)
        {
            throw new ArgumentException($"{shipType} does not belong to side {expectedSide}.", parameterName);
        }
    }
}

/// <summary>
/// Deterministic side-specific shuffle bag used by sampled multi-ship episodes. A ship type cannot
/// repeat on a side until every candidate in that side's pool has been emitted once.
/// </summary>
internal sealed class RlShipTypeShuffleBag
{
    private readonly List<ConfigData.ShipTypes> _cycle = new List<ConfigData.ShipTypes>();
    private readonly Random _random;
    private int _nextIndex;

    internal RlShipTypeShuffleBag(
        IReadOnlyList<ConfigData.ShipTypes> shipTypes,
        int expectedSide,
        int seed)
    {
        if (shipTypes == null || shipTypes.Count == 0)
        {
            throw new ArgumentException("At least one ship type is required.", nameof(shipTypes));
        }

        for (int index = 0; index < shipTypes.Count; index++)
        {
            ConfigData.ShipTypes shipType = shipTypes[index];
            RlOneVsOneMatchupSampler.ValidateSide(shipType, expectedSide, nameof(shipTypes));
            _cycle.Add(shipType);
        }

        _random = new Random(seed);
        ShuffleCycle();
    }

    internal ConfigData.ShipTypes Next()
    {
        if (_nextIndex >= _cycle.Count)
        {
            ShuffleCycle();
        }

        return _cycle[_nextIndex++];
    }

    private void ShuffleCycle()
    {
        for (int index = _cycle.Count - 1; index > 0; index--)
        {
            int swapIndex = _random.Next(index + 1);
            ConfigData.ShipTypes temporary = _cycle[index];
            _cycle[index] = _cycle[swapIndex];
            _cycle[swapIndex] = temporary;
        }
        _nextIndex = 0;
    }
}

/// <summary>
/// Holds the sampled composition selected for the current episode. One-ship sampled training keeps
/// the original shuffled Cartesian pair sequence exactly. Multi-ship sampled training uses one
/// deterministic shuffle bag per side so an episode can contain mixed ship types without starving
/// any candidate. GetShipType is stable for the full episode; only PrepareEpisode advances sampling.
/// </summary>
internal sealed class RlOneVsOneEpisodeMatchupSelector
{
    private const int BeeShuffleSeedOffset = 48611;
    private const int HumanShuffleSeedOffset = 104729;

    private readonly RlOneVsOneTrainingOptions _options;
    private readonly RlOneVsOneMatchupSampler _sampler;
    private readonly RlShipTypeShuffleBag _beeShuffleBag;
    private readonly RlShipTypeShuffleBag _humanShuffleBag;
    private readonly ConfigData.ShipTypes[] _currentBeeComposition;
    private readonly ConfigData.ShipTypes[] _currentHumanComposition;
    private RlOneVsOneMatchup _currentMatchup;
    private bool _hasPreparedSampledMatchup;

    internal RlOneVsOneEpisodeMatchupSelector(RlOneVsOneTrainingOptions options)
        : this(options, Guid.NewGuid().GetHashCode())
    {
    }

    internal RlOneVsOneEpisodeMatchupSelector(RlOneVsOneTrainingOptions options, int seed)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MatchupMode != RlOneVsOneMatchupMode.Sampled)
        {
            return;
        }

        if (_options.ShipsPerSide == 1)
        {
            // This is intentionally the exact historical construction so seeded 1v1 training keeps
            // its existing shuffled Cartesian sequence byte-for-byte in behavior.
            _sampler = new RlOneVsOneMatchupSampler(options.BeeShipTypes, options.HumanShipTypes, seed);
            return;
        }

        _beeShuffleBag = new RlShipTypeShuffleBag(
            options.BeeShipTypes,
            ConfigData.Configuration.BeeSide,
            unchecked(seed * 31 + BeeShuffleSeedOffset));
        _humanShuffleBag = new RlShipTypeShuffleBag(
            options.HumanShipTypes,
            ConfigData.Configuration.HumanSide,
            unchecked(seed * 31 + HumanShuffleSeedOffset));
        _currentBeeComposition = new ConfigData.ShipTypes[_options.ShipsPerSide];
        _currentHumanComposition = new ConfigData.ShipTypes[_options.ShipsPerSide];
    }

    internal void PrepareEpisode()
    {
        if (_sampler != null)
        {
            _currentMatchup = _sampler.Next();
            _hasPreparedSampledMatchup = true;
            return;
        }

        if (_beeShuffleBag == null || _humanShuffleBag == null)
        {
            return;
        }

        for (int shipIndex = 0; shipIndex < _options.ShipsPerSide; shipIndex++)
        {
            _currentBeeComposition[shipIndex] = _beeShuffleBag.Next();
            _currentHumanComposition[shipIndex] = _humanShuffleBag.Next();
        }
        _hasPreparedSampledMatchup = true;
    }

    internal ConfigData.ShipTypes GetShipType(int side, int shipIndex)
    {
        if (_options.MatchupMode == RlOneVsOneMatchupMode.Fixed)
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                return _options.GetBeeShipType(shipIndex);
            }
            if (side == ConfigData.Configuration.HumanSide)
            {
                return _options.GetHumanShipType(shipIndex);
            }
            throw new ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
        }

        if (!_hasPreparedSampledMatchup)
        {
            throw new InvalidOperationException("Sampled RL matchup must be prepared before ships are spawned.");
        }
        if (shipIndex < 0 || shipIndex >= _options.ShipsPerSide)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }

        if (_sampler != null)
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                return _currentMatchup.BeeShipType;
            }
            if (side == ConfigData.Configuration.HumanSide)
            {
                return _currentMatchup.HumanShipType;
            }
        }
        else
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                return _currentBeeComposition[shipIndex];
            }
            if (side == ConfigData.Configuration.HumanSide)
            {
                return _currentHumanComposition[shipIndex];
            }
        }

        throw new ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
    }
}

/// <summary>
/// Process-wide training-only facade. Level.SetupShips always prepares the AI side first, so the
/// episode composition is advanced once before either side is spawned and then remains fixed for both sides.
/// </summary>
internal static class RlOneVsOneEpisodeMatchups
{
    private static RlOneVsOneEpisodeMatchupSelector _selector;

    private static RlOneVsOneEpisodeMatchupSelector Selector
    {
        get
        {
            if (_selector == null)
            {
                RlOneVsOneTrainingOptions options = RlOneVsOneTrainingOptions.Parse(Environment.GetCommandLineArgs());
                _selector = new RlOneVsOneEpisodeMatchupSelector(options);
            }
            return _selector;
        }
    }

    internal static void PrepareEpisode()
    {
        Selector.PrepareEpisode();
    }

    internal static ConfigData.ShipTypes GetShipType(int side, int shipIndex)
    {
        return Selector.GetShipType(side, shipIndex);
    }
}
