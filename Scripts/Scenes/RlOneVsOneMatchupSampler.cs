using Assets.Scripts;
using System;
using System.Collections.Generic;
using System.Text;

internal static class RlShipCombatCapability
{
    internal static bool IsWeaponless(ConfigData.ShipTypes shipType)
    {
        switch (shipType)
        {
            case ConfigData.ShipTypes.Honeybee:
            case ConfigData.ShipTypes.Scout:
            case ConfigData.ShipTypes.WarpGate:
            case ConfigData.ShipTypes.Factory:
            case ConfigData.ShipTypes.CarpenterBee:
            case ConfigData.ShipTypes.Beehive:
                return true;
            default:
                return false;
        }
    }

    internal static bool HasAnyWeapon(IReadOnlyList<ConfigData.ShipTypes> composition)
    {
        if (composition == null)
        {
            return false;
        }
        for (int i = 0; i < composition.Count; i++)
        {
            if (!IsWeaponless(composition[i]))
            {
                return true;
            }
        }
        return false;
    }
}

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
/// Produces a shuffled Cartesian cycle of valid one-ship Bee x Human matchups. A one-ship side made
/// from a weaponless type is excluded because that side cannot participate in combat.
/// </summary>
internal sealed class RlOneVsOneMatchupSampler
{
    private readonly List<RlOneVsOneMatchup> _cycle = new List<RlOneVsOneMatchup>();
    private readonly Random _random;
    private int _nextIndex;

    internal int MatchupCount => _cycle.Count;

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

        int beeSide = GetSideForShipType(ConfigData.ShipTypes.Wasp);
        int humanSide = GetSideForShipType(ConfigData.ShipTypes.Gunship);
        for (int beeIndex = 0; beeIndex < beeShipTypes.Count; beeIndex++)
        {
            ConfigData.ShipTypes beeShipType = beeShipTypes[beeIndex];
            ValidateSide(beeShipType, beeSide, nameof(beeShipTypes));
            for (int humanIndex = 0; humanIndex < humanShipTypes.Count; humanIndex++)
            {
                ConfigData.ShipTypes humanShipType = humanShipTypes[humanIndex];
                ValidateSide(humanShipType, humanSide, nameof(humanShipTypes));
                if (RlShipCombatCapability.IsWeaponless(beeShipType) ||
                    RlShipCombatCapability.IsWeaponless(humanShipType))
                {
                    continue;
                }
                _cycle.Add(new RlOneVsOneMatchup(beeShipType, humanShipType));
            }
        }

        if (_cycle.Count == 0)
        {
            throw new ArgumentException("Sampled one-ship matchups require an armed candidate on both sides.");
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

    internal static int GetSideForShipType(ConfigData.ShipTypes shipType)
    {
        int side;
        if (!Utilities.ConvertShipTypeToSide.TryGetValue(shipType, out side))
        {
            throw new InvalidOperationException($"No faction side is registered for ship type {shipType}.");
        }
        return side;
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
/// Uniformly samples unordered fleet compositions with replacement. For N candidate ship types and K
/// ships there are C(N+K-1,K) compositions; each rank in that set is equally likely. Ranks whose
/// complete composition is weaponless are rejected, leaving every combat-capable composition with
/// the same accepted probability. Slot order is shuffled after the multiset is selected so formation
/// positions do not become coupled to the canonical combination ordering.
/// </summary>
internal sealed class RlShipCompositionSampler
{
    private readonly ConfigData.ShipTypes[] _shipTypes;
    private readonly int _shipsPerSide;
    private readonly Random _random;

    internal long CombinationCount { get; }
    internal long ValidCombinationCount { get; }

    internal RlShipCompositionSampler(
        IReadOnlyList<ConfigData.ShipTypes> shipTypes,
        int shipsPerSide,
        int expectedSide,
        int seed)
    {
        if (shipTypes == null || shipTypes.Count == 0)
        {
            throw new ArgumentException("At least one ship type is required.", nameof(shipTypes));
        }
        if (shipsPerSide <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shipsPerSide));
        }

        _shipTypes = new ConfigData.ShipTypes[shipTypes.Count];
        bool hasArmedCandidate = false;
        int weaponlessCandidateCount = 0;
        for (int index = 0; index < shipTypes.Count; index++)
        {
            ConfigData.ShipTypes shipType = shipTypes[index];
            RlOneVsOneMatchupSampler.ValidateSide(shipType, expectedSide, nameof(shipTypes));
            _shipTypes[index] = shipType;
            if (RlShipCombatCapability.IsWeaponless(shipType))
            {
                weaponlessCandidateCount++;
            }
            else
            {
                hasArmedCandidate = true;
            }
        }
        if (!hasArmedCandidate)
        {
            throw new ArgumentException("At least one sampled ship type must have a weapon.", nameof(shipTypes));
        }

        _shipsPerSide = shipsPerSide;
        CombinationCount = Choose(_shipTypes.Length + shipsPerSide - 1, shipsPerSide);
        if (CombinationCount <= 0)
        {
            throw new InvalidOperationException("RL fleet composition count overflowed or was empty.");
        }
        long weaponlessCombinationCount = weaponlessCandidateCount == 0
            ? 0
            : Choose(weaponlessCandidateCount + shipsPerSide - 1, shipsPerSide);
        ValidCombinationCount = CombinationCount - weaponlessCombinationCount;
        if (ValidCombinationCount <= 0)
        {
            throw new InvalidOperationException("RL fleet composition sampler has no combat-capable combinations.");
        }
        _random = new Random(seed);
    }

    internal ConfigData.ShipTypes[] Next()
    {
        while (true)
        {
            long rank = NextLong(_random, CombinationCount);
            ConfigData.ShipTypes[] composition = CreateCompositionForRank(rank);
            if (!RlShipCombatCapability.HasAnyWeapon(composition))
            {
                continue;
            }
            ShuffleSlots(composition);
            return composition;
        }
    }

    internal ConfigData.ShipTypes[] CreateCompositionForRank(long rank)
    {
        if (rank < 0 || rank >= CombinationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        ConfigData.ShipTypes[] composition = new ConfigData.ShipTypes[_shipsPerSide];
        int minimumTypeIndex = 0;
        long remainingRank = rank;
        for (int slot = 0; slot < _shipsPerSide; slot++)
        {
            int remainingSlots = _shipsPerSide - slot;
            for (int typeIndex = minimumTypeIndex; typeIndex < _shipTypes.Length; typeIndex++)
            {
                long suffixCount = remainingSlots == 1
                    ? 1
                    : Choose((_shipTypes.Length - typeIndex) + remainingSlots - 2, remainingSlots - 1);
                if (remainingRank < suffixCount)
                {
                    composition[slot] = _shipTypes[typeIndex];
                    minimumTypeIndex = typeIndex;
                    break;
                }
                remainingRank -= suffixCount;
            }
        }
        return composition;
    }

    private void ShuffleSlots(ConfigData.ShipTypes[] composition)
    {
        for (int index = composition.Length - 1; index > 0; index--)
        {
            int swapIndex = _random.Next(index + 1);
            ConfigData.ShipTypes temporary = composition[index];
            composition[index] = composition[swapIndex];
            composition[swapIndex] = temporary;
        }
    }

    private static long Choose(int n, int k)
    {
        if (k < 0 || n < 0 || k > n)
        {
            return 0;
        }
        k = Math.Min(k, n - k);
        long result = 1;
        for (int i = 1; i <= k; i++)
        {
            checked
            {
                result = result * (n - k + i) / i;
            }
        }
        return result;
    }

    private static long NextLong(Random random, long maxExclusive)
    {
        if (maxExclusive <= 1)
        {
            return 0;
        }
        return (long)(random.NextDouble() * maxExclusive);
    }
}

/// <summary>
/// Holds the sampled composition selected for the current episode. One-ship training preserves the
/// shuffled Cartesian coverage contract, while multi-ship training samples unordered compositions
/// uniformly and then randomizes their formation-slot order. Imbalanced recent matchup results add
/// 0.5x, 1x or 2x replay weight on top of every matchup's permanent 1x baseline weight.
/// </summary>
internal sealed class RlOneVsOneEpisodeMatchupSelector
{
    internal const double DefaultPriorityWeightScale = 1.0;
    internal const int DefaultPriorityOutcomeWindow = 32;
    internal const int DefaultPriorityMinimumSamples = 4;

    private const int BeeCompositionSeedOffset = 48611;
    private const int HumanCompositionSeedOffset = 104729;
    private const int PrioritySeedOffset = 130363;

    private sealed class MatchupHistoryState
    {
        internal readonly ConfigData.ShipTypes[] BeeComposition;
        internal readonly ConfigData.ShipTypes[] HumanComposition;
        private readonly Queue<float> _beeScores = new Queue<float>();
        private float _beeScoreSum;

        internal int OutcomeCount => _beeScores.Count;
        internal float BeeScoreRate => _beeScores.Count > 0 ? _beeScoreSum / _beeScores.Count : 0.5f;

        internal MatchupHistoryState(
            ConfigData.ShipTypes[] beeComposition,
            ConfigData.ShipTypes[] humanComposition)
        {
            BeeComposition = beeComposition;
            HumanComposition = humanComposition;
        }

        internal void AddOutcome(float beeScore, int window)
        {
            _beeScores.Enqueue(beeScore);
            _beeScoreSum += beeScore;
            while (_beeScores.Count > window)
            {
                _beeScoreSum -= _beeScores.Dequeue();
            }
        }
    }

    private readonly RlOneVsOneTrainingOptions _options;
    private readonly RlOneVsOneMatchupSampler _sampler;
    private readonly RlShipCompositionSampler _beeCompositionSampler;
    private readonly RlShipCompositionSampler _humanCompositionSampler;
    private readonly ConfigData.ShipTypes[] _currentBeeComposition;
    private readonly ConfigData.ShipTypes[] _currentHumanComposition;
    private readonly Dictionary<string, MatchupHistoryState> _priorityHistory =
        new Dictionary<string, MatchupHistoryState>();
    private readonly Random _priorityRandom;
    private readonly double _priorityWeightScale;
    private readonly int _priorityOutcomeWindow;
    private readonly int _priorityMinimumSamples;
    private readonly int _beeSide;
    private readonly int _humanSide;
    private readonly double _baselineMatchupWeight;
    private RlOneVsOneMatchup _currentMatchup;
    private bool _hasPreparedSampledMatchup;
    private bool _currentOutcomeRecorded;

    internal RlOneVsOneEpisodeMatchupSelector(RlOneVsOneTrainingOptions options)
        : this(options, Guid.NewGuid().GetHashCode())
    {
    }

    internal RlOneVsOneEpisodeMatchupSelector(RlOneVsOneTrainingOptions options, int seed)
        : this(
            options,
            seed,
            DefaultPriorityWeightScale,
            DefaultPriorityOutcomeWindow,
            DefaultPriorityMinimumSamples)
    {
    }

    internal RlOneVsOneEpisodeMatchupSelector(
        RlOneVsOneTrainingOptions options,
        int seed,
        double priorityWeightScale,
        int priorityOutcomeWindow,
        int priorityMinimumSamples)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (double.IsNaN(priorityWeightScale) || double.IsInfinity(priorityWeightScale) || priorityWeightScale < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(priorityWeightScale));
        }
        if (priorityOutcomeWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priorityOutcomeWindow));
        }
        if (priorityMinimumSamples <= 0 || priorityMinimumSamples > priorityOutcomeWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(priorityMinimumSamples));
        }

        _priorityWeightScale = priorityWeightScale;
        _priorityOutcomeWindow = priorityOutcomeWindow;
        _priorityMinimumSamples = priorityMinimumSamples;

        if (_options.MatchupMode != RlOneVsOneMatchupMode.Sampled)
        {
            return;
        }

        _beeSide = RlOneVsOneMatchupSampler.GetSideForShipType(ConfigData.ShipTypes.Wasp);
        _humanSide = RlOneVsOneMatchupSampler.GetSideForShipType(ConfigData.ShipTypes.Gunship);
        _priorityRandom = new Random(unchecked(seed * 31 + PrioritySeedOffset));

        if (_options.ShipsPerSide == 1)
        {
            _sampler = new RlOneVsOneMatchupSampler(options.BeeShipTypes, options.HumanShipTypes, seed);
            _baselineMatchupWeight = _sampler.MatchupCount;
            return;
        }

        _beeCompositionSampler = new RlShipCompositionSampler(
            options.BeeShipTypes,
            options.ShipsPerSide,
            _beeSide,
            unchecked(seed * 31 + BeeCompositionSeedOffset));
        _humanCompositionSampler = new RlShipCompositionSampler(
            options.HumanShipTypes,
            options.ShipsPerSide,
            _humanSide,
            unchecked(seed * 31 + HumanCompositionSeedOffset));
        _currentBeeComposition = new ConfigData.ShipTypes[_options.ShipsPerSide];
        _currentHumanComposition = new ConfigData.ShipTypes[_options.ShipsPerSide];
        _baselineMatchupWeight = (double)_beeCompositionSampler.ValidCombinationCount *
            _humanCompositionSampler.ValidCombinationCount;
    }

    internal void PrepareEpisode()
    {
        if (_options.MatchupMode != RlOneVsOneMatchupMode.Sampled)
        {
            return;
        }

        if (!TryPreparePrioritizedMatchup())
        {
            PrepareBaselineMatchup();
        }
        _hasPreparedSampledMatchup = true;
        _currentOutcomeRecorded = false;
    }

    internal void RecordEpisodeOutcome(int winningSide, bool timedOut)
    {
        if (_options.MatchupMode != RlOneVsOneMatchupMode.Sampled ||
            !_hasPreparedSampledMatchup || _currentOutcomeRecorded)
        {
            return;
        }

        float beeScore;
        if (timedOut || winningSide == 0)
        {
            beeScore = 0.5f;
        }
        else if (winningSide == _beeSide)
        {
            beeScore = 1f;
        }
        else if (winningSide == _humanSide)
        {
            beeScore = 0f;
        }
        else
        {
            return;
        }

        ConfigData.ShipTypes[] beeComposition = GetCurrentCanonicalComposition(true);
        ConfigData.ShipTypes[] humanComposition = GetCurrentCanonicalComposition(false);
        string key = BuildMatchupKey(beeComposition, humanComposition);
        MatchupHistoryState state;
        if (!_priorityHistory.TryGetValue(key, out state))
        {
            state = new MatchupHistoryState(beeComposition, humanComposition);
            _priorityHistory.Add(key, state);
        }
        state.AddOutcome(beeScore, _priorityOutcomeWindow);
        _currentOutcomeRecorded = true;
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

    internal static float CalculatePriorityExtraWeight(float beeScoreRate)
    {
        float boundedRate = Math.Max(0f, Math.Min(1f, beeScoreRate));
        float imbalance = Math.Abs(boundedRate - 0.5f);
        if (imbalance <= 0.10f)
        {
            return 0f;
        }
        if (imbalance <= 0.25f)
        {
            return 0.5f;
        }
        if (imbalance <= 0.40f)
        {
            return 1f;
        }
        return 2f;
    }

    internal static double CalculatePriorityReplayProbability(double baselineWeight, double totalExtraWeight)
    {
        if (baselineWeight <= 0d || totalExtraWeight <= 0d)
        {
            return 0d;
        }
        return totalExtraWeight / (baselineWeight + totalExtraWeight);
    }

    private void PrepareBaselineMatchup()
    {
        if (_sampler != null)
        {
            _currentMatchup = _sampler.Next();
            return;
        }
        if (_beeCompositionSampler == null || _humanCompositionSampler == null)
        {
            throw new InvalidOperationException("Sampled RL matchup selector was not initialized.");
        }

        CopyComposition(_beeCompositionSampler.Next(), _currentBeeComposition);
        CopyComposition(_humanCompositionSampler.Next(), _currentHumanComposition);
    }

    private bool TryPreparePrioritizedMatchup()
    {
        if (_priorityRandom == null || _priorityWeightScale <= 0d || _priorityHistory.Count == 0)
        {
            return false;
        }

        double totalExtraWeight = 0d;
        foreach (MatchupHistoryState state in _priorityHistory.Values)
        {
            totalExtraWeight += GetEligiblePriorityExtraWeight(state) * _priorityWeightScale;
        }
        double replayProbability = CalculatePriorityReplayProbability(_baselineMatchupWeight, totalExtraWeight);
        if (replayProbability <= 0d || _priorityRandom.NextDouble() >= replayProbability)
        {
            return false;
        }

        double roll = _priorityRandom.NextDouble() * totalExtraWeight;
        MatchupHistoryState lastEligible = null;
        foreach (MatchupHistoryState state in _priorityHistory.Values)
        {
            double extraWeight = GetEligiblePriorityExtraWeight(state) * _priorityWeightScale;
            if (extraWeight <= 0d)
            {
                continue;
            }
            lastEligible = state;
            if (roll < extraWeight)
            {
                ApplyPrioritizedMatchup(state);
                return true;
            }
            roll -= extraWeight;
        }

        if (lastEligible != null)
        {
            ApplyPrioritizedMatchup(lastEligible);
            return true;
        }
        return false;
    }

    private float GetEligiblePriorityExtraWeight(MatchupHistoryState state)
    {
        return state.OutcomeCount < _priorityMinimumSamples
            ? 0f
            : CalculatePriorityExtraWeight(state.BeeScoreRate);
    }

    private void ApplyPrioritizedMatchup(MatchupHistoryState state)
    {
        if (_sampler != null)
        {
            _currentMatchup = new RlOneVsOneMatchup(state.BeeComposition[0], state.HumanComposition[0]);
            return;
        }

        CopyComposition(state.BeeComposition, _currentBeeComposition);
        CopyComposition(state.HumanComposition, _currentHumanComposition);
        ShuffleCompositionSlots(_currentBeeComposition);
        ShuffleCompositionSlots(_currentHumanComposition);
    }

    private ConfigData.ShipTypes[] GetCurrentCanonicalComposition(bool beeSide)
    {
        if (_sampler != null)
        {
            return new[] { beeSide ? _currentMatchup.BeeShipType : _currentMatchup.HumanShipType };
        }
        return CreateCanonicalComposition(beeSide ? _currentBeeComposition : _currentHumanComposition);
    }

    private static ConfigData.ShipTypes[] CreateCanonicalComposition(IReadOnlyList<ConfigData.ShipTypes> source)
    {
        ConfigData.ShipTypes[] copy = new ConfigData.ShipTypes[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i];
        }
        Array.Sort(copy, (left, right) => ((int)left).CompareTo((int)right));
        return copy;
    }

    private static string BuildMatchupKey(
        IReadOnlyList<ConfigData.ShipTypes> beeComposition,
        IReadOnlyList<ConfigData.ShipTypes> humanComposition)
    {
        StringBuilder builder = new StringBuilder();
        AppendCompositionKey(builder, beeComposition);
        builder.Append('|');
        AppendCompositionKey(builder, humanComposition);
        return builder.ToString();
    }

    private static void AppendCompositionKey(StringBuilder builder, IReadOnlyList<ConfigData.ShipTypes> composition)
    {
        for (int i = 0; i < composition.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append((int)composition[i]);
        }
    }

    private void ShuffleCompositionSlots(ConfigData.ShipTypes[] composition)
    {
        for (int index = composition.Length - 1; index > 0; index--)
        {
            int swapIndex = _priorityRandom.Next(index + 1);
            ConfigData.ShipTypes temporary = composition[index];
            composition[index] = composition[swapIndex];
            composition[swapIndex] = temporary;
        }
    }

    private static void CopyComposition(ConfigData.ShipTypes[] source, ConfigData.ShipTypes[] destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = source[i];
        }
    }
}

internal static class RlOneVsOneEpisodeMatchups
{
    private static RlOneVsOneEpisodeMatchupSelector _selector;

    static RlOneVsOneEpisodeMatchups()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

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

    private static void HandleEpisodeEnded(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        _selector?.RecordEpisodeOutcome(result.WinningSide, result.TimedOut);
    }
}