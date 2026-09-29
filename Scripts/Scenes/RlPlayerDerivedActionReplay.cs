using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// Loads immutable, operator-reviewed action replay artifacts for player-derived adversarial 1v1
/// scenarios. The replay side is scripted movement/turret aim/fire/capability only; the opposing
/// policy still produces fresh PPO actions. Recorded world-space actions are rotated from the source
/// spawn axis to the current randomized training spawn axis so arena rotation remains useful
/// augmentation. Legacy movement/aim/fire-only replay artifacts remain loadable.
/// </summary>
internal static class RlPlayerDerivedActionReplay
{
    internal const string CatalogFlag = "--bees-adversarial-replay-catalog";
    internal const int CatalogSchemaVersion = 1;
    internal const int ContinuousActionCount = RlOneVsOneAgent.ContinuousActionCount;
    internal const int WeaponFireBranchCount = RlOneVsOneAgent.WeaponFireBranchCount;
    internal const int MaximumReplayFrames = 2400;
    internal const int ExpectedFixedStepInterval = 5;

    private const int LegacyContinuousActionCount = 34;
    private const string LegacyReplayMagic = "BEESRPL1";
    private const string CapabilityLegacyReplayMagic = "BEESRPL2";
    private const string ReplayMagic = "BEESRPL3";
    private const string PlayerDerivedTagPrefix = "player-derived:";
    private const int ReplayHeaderBytes = 24;
    private const int LegacyReplayFrameBytes = LegacyContinuousActionCount * sizeof(float) + sizeof(ushort);
    private const int CapabilityLegacyReplayFrameBytes = LegacyReplayFrameBytes + sizeof(byte);
    private const int ReplayFrameBytes = ContinuousActionCount * sizeof(float) + sizeof(ushort) + sizeof(byte);

    [Serializable]
    private sealed class ReplayCatalog
    {
        public int schemaVersion;
        public string catalogSha256;
        public ReplayCatalogEntry[] entries;
    }

    [Serializable]
    private sealed class ReplayCatalogEntry
    {
        public string scenarioId;
        public string side;
        public string replayId;
        public string replayPath;
        public string replaySha256;
        public int frameCount;
        public int fixedStepInterval;
    }

    internal sealed class ReplayData
    {
        internal readonly string ScenarioId;
        internal readonly string Side;
        internal readonly string ReplayId;
        internal readonly int FrameCount;
        internal readonly int FixedStepInterval;
        internal readonly Vector2 SourceStartDirection;
        internal readonly float[] ContinuousActions;
        internal readonly ushort[] FireMasks;
        internal readonly byte[] SpecialActions;

        internal ReplayData(
            string scenarioId,
            string side,
            string replayId,
            int frameCount,
            int fixedStepInterval,
            Vector2 sourceStartDirection,
            float[] continuousActions,
            ushort[] fireMasks,
            byte[] specialActions)
        {
            ScenarioId = scenarioId;
            Side = side;
            ReplayId = replayId;
            FrameCount = frameCount;
            FixedStepInterval = fixedStepInterval;
            SourceStartDirection = sourceStartDirection;
            ContinuousActions = continuousActions;
            FireMasks = fireMasks;
            SpecialActions = specialActions;
        }
    }

    private static readonly Dictionary<string, ReplayData> Replays =
        new Dictionary<string, ReplayData>(StringComparer.Ordinal);
    private static readonly Dictionary<Level, RlPlayerDerivedActionReplayController> Controllers =
        new Dictionary<Level, RlPlayerDerivedActionReplayController>();
    private static bool _loaded;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        Replays.Clear();
        Controllers.Clear();
        _loaded = false;
    }

    internal static void PrepareEpisode(Level level)
    {
        if (level == null)
        {
            return;
        }

        EnsureLoaded(Environment.GetCommandLineArgs());
        ReplayData replay = null;
        TryGetCurrent(level, out replay);
        if (!Controllers.TryGetValue(level, out RlPlayerDerivedActionReplayController controller) || controller == null)
        {
            controller = level.GetComponent<RlPlayerDerivedActionReplayController>();
            if (controller == null)
            {
                controller = level.gameObject.AddComponent<RlPlayerDerivedActionReplayController>();
            }
            Controllers[level] = controller;
        }
        controller.Prepare(level, replay);
    }

    internal static void EndEpisode(Level level)
    {
        if (level != null && Controllers.TryGetValue(level, out RlPlayerDerivedActionReplayController controller) &&
            controller != null)
        {
            controller.EndReplayEpisode();
        }
    }

    internal static bool IsScriptedSide(Level level, int side)
    {
        if (ConfigData.Configuration == null || !TryGetCurrent(level, out ReplayData replay))
        {
            return false;
        }
        int scriptedSide = replay.Side == "Bee"
            ? ConfigData.Configuration.BeeSide
            : ConfigData.Configuration.HumanSide;
        return side == scriptedSide;
    }

    internal static bool TryGetCurrent(Level level, out ReplayData replay)
    {
        replay = null;
        if (level == null)
        {
            return false;
        }
        EnsureLoaded(Environment.GetCommandLineArgs());
        string pressureTag = RlOneVsOnePerArenaMatchups.GetCurrentPressureTag(level);
        if (string.IsNullOrEmpty(pressureTag) ||
            !pressureTag.StartsWith(PlayerDerivedTagPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        string scenarioId = pressureTag.Substring(PlayerDerivedTagPrefix.Length);
        return Replays.TryGetValue(scenarioId, out replay);
    }

    internal static IReadOnlyDictionary<string, ReplayData> LoadCatalogForTests(string catalogPath)
    {
        return LoadCatalog(catalogPath);
    }

    internal static void ResetForTests()
    {
        ResetForSceneLoad();
    }

    private static void EnsureLoaded(string[] args)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        string catalogPath = ReadCatalogPath(args);
        if (catalogPath == null)
        {
            return;
        }
        IReadOnlyDictionary<string, ReplayData> loaded = LoadCatalog(catalogPath);
        foreach (KeyValuePair<string, ReplayData> pair in loaded)
        {
            Replays.Add(pair.Key, pair.Value);
        }
    }

    private static IReadOnlyDictionary<string, ReplayData> LoadCatalog(string catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            throw new ArgumentException($"{CatalogFlag} requires a catalog path.");
        }
        string fullCatalogPath = Path.GetFullPath(catalogPath.Trim().Trim('"'));
        if (!File.Exists(fullCatalogPath))
        {
            throw new ArgumentException($"{CatalogFlag} file does not exist: {fullCatalogPath}");
        }

        ReplayCatalog catalog;
        try
        {
            catalog = JsonUtility.FromJson<ReplayCatalog>(File.ReadAllText(fullCatalogPath));
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                $"Could not parse {CatalogFlag} catalog {fullCatalogPath}: " +
                $"{exception.GetType().Name}: {exception.Message}",
                exception);
        }
        if (catalog == null || catalog.schemaVersion != CatalogSchemaVersion || catalog.entries == null ||
            !IsLowerHex(catalog.catalogSha256, 64))
        {
            throw new ArgumentException($"{CatalogFlag} catalog is incompatible: {fullCatalogPath}");
        }
        if (catalog.entries.Length > RlPlayerDerivedAdversarialPressure.MaximumScenarioCount)
        {
            throw new ArgumentException($"{CatalogFlag} contains too many replay entries.");
        }
        string expectedCatalogSha256 = ComputeCatalogIdentitySha256(catalog);
        if (!catalog.catalogSha256.Equals(expectedCatalogSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{CatalogFlag} catalog identity hash mismatch: expected {catalog.catalogSha256}, " +
                $"computed {expectedCatalogSha256}.");
        }
        string expectedCatalogFileName = $"catalog-{expectedCatalogSha256.Substring(0, 24)}.json";
        if (!Path.GetFileName(fullCatalogPath).Equals(expectedCatalogFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{CatalogFlag} catalog filename is not content-addressed to its identity: " +
                $"expected {expectedCatalogFileName}.");
        }

        Dictionary<string, ReplayData> result =
            new Dictionary<string, ReplayData>(StringComparer.Ordinal);
        string catalogDirectory = Path.GetDirectoryName(fullCatalogPath);
        if (string.IsNullOrEmpty(catalogDirectory))
        {
            throw new ArgumentException($"{CatalogFlag} catalog directory could not be resolved.");
        }
        for (int i = 0; i < catalog.entries.Length; i++)
        {
            ReplayCatalogEntry entry = catalog.entries[i];
            ValidateCatalogEntry(entry);
            if (result.ContainsKey(entry.scenarioId))
            {
                throw new ArgumentException($"{CatalogFlag} contains duplicate scenario {entry.scenarioId}.");
            }
            string replayPath = Path.GetFullPath(
                Path.Combine(catalogDirectory, entry.replayPath.Replace('/', Path.DirectorySeparatorChar)));
            result.Add(entry.scenarioId, ReadReplay(entry, replayPath));
        }
        return result;
    }

    private static void ValidateCatalogEntry(ReplayCatalogEntry entry)
    {
        if (entry == null || !IsContentId(entry.scenarioId, "adv-", 24))
        {
            throw new ArgumentException($"{CatalogFlag} contains an invalid scenario ID.");
        }
        if (entry.side != "Bee" && entry.side != "Human")
        {
            throw new ArgumentException($"{CatalogFlag} scenario {entry.scenarioId} has invalid replay side.");
        }
        if (!IsContentId(entry.replayId, "advreplay-", 24))
        {
            throw new ArgumentException($"{CatalogFlag} scenario {entry.scenarioId} has invalid replay ID.");
        }
        if (string.IsNullOrWhiteSpace(entry.replayPath) || Path.IsPathRooted(entry.replayPath))
        {
            throw new ArgumentException(
                $"{CatalogFlag} scenario {entry.scenarioId} replay path must be catalog-relative.");
        }
        if (!IsLowerHex(entry.replaySha256, 64))
        {
            throw new ArgumentException($"{CatalogFlag} scenario {entry.scenarioId} has invalid replay hash.");
        }
        if (entry.frameCount < 1 || entry.frameCount > MaximumReplayFrames ||
            entry.fixedStepInterval != ExpectedFixedStepInterval)
        {
            throw new ArgumentException($"{CatalogFlag} scenario {entry.scenarioId} has invalid replay bounds.");
        }
    }

    private static string ComputeCatalogIdentitySha256(ReplayCatalog catalog)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("{\"entries\":[");
        for (int i = 0; i < catalog.entries.Length; i++)
        {
            ReplayCatalogEntry entry = catalog.entries[i];
            ValidateCatalogEntry(entry);
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append("{\"fixedStepInterval\":");
            builder.Append(entry.fixedStepInterval.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"frameCount\":");
            builder.Append(entry.frameCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"replayId\":");
            AppendCanonicalJsonString(builder, entry.replayId);
            builder.Append(",\"replayPath\":");
            AppendCanonicalJsonString(builder, entry.replayPath);
            builder.Append(",\"replaySha256\":");
            AppendCanonicalJsonString(builder, entry.replaySha256);
            builder.Append(",\"scenarioId\":");
            AppendCanonicalJsonString(builder, entry.scenarioId);
            builder.Append(",\"side\":");
            AppendCanonicalJsonString(builder, entry.side);
            builder.Append('}');
        }
        builder.Append("],\"schemaVersion\":");
        builder.Append(catalog.schemaVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
        return ComputeSha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void AppendCanonicalJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    private static ReplayData ReadReplay(ReplayCatalogEntry entry, string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Replay artifact does not exist for {entry.scenarioId}: {path}");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string actualHash = ComputeSha256(bytes);
        if (!actualHash.Equals(entry.replaySha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Replay artifact hash mismatch for {entry.scenarioId}: expected {entry.replaySha256}, got {actualHash}.");
        }
        if (bytes.Length < ReplayHeaderBytes)
        {
            throw new ArgumentException($"Replay artifact is truncated for {entry.scenarioId}.");
        }

        using (MemoryStream stream = new MemoryStream(bytes, false))
        using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, false))
        {
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
            bool capabilityAware;
            bool legacyActionLayout;
            int encodedContinuousActionCount;
            int frameBytes;
            if (magic == ReplayMagic)
            {
                capabilityAware = true;
                legacyActionLayout = false;
                encodedContinuousActionCount = ContinuousActionCount;
                frameBytes = ReplayFrameBytes;
            }
            else if (magic == CapabilityLegacyReplayMagic)
            {
                capabilityAware = true;
                legacyActionLayout = true;
                encodedContinuousActionCount = LegacyContinuousActionCount;
                frameBytes = CapabilityLegacyReplayFrameBytes;
            }
            else if (magic == LegacyReplayMagic)
            {
                capabilityAware = false;
                legacyActionLayout = true;
                encodedContinuousActionCount = LegacyContinuousActionCount;
                frameBytes = LegacyReplayFrameBytes;
            }
            else
            {
                throw new ArgumentException($"Replay artifact header is incompatible for {entry.scenarioId}.");
            }

            int frameCount = reader.ReadInt32();
            int fixedStepInterval = reader.ReadInt32();
            Vector2 sourceDirection = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            long expectedLength = ReplayHeaderBytes + (long)entry.frameCount * frameBytes;
            if (bytes.LongLength != expectedLength)
            {
                throw new ArgumentException(
                    $"Replay artifact size mismatch for {entry.scenarioId}: expected {expectedLength}, got {bytes.LongLength}.");
            }
            if (frameCount != entry.frameCount || fixedStepInterval != entry.fixedStepInterval ||
                !IsFinite(sourceDirection.x) || !IsFinite(sourceDirection.y) ||
                Mathf.Abs(sourceDirection.magnitude - 1f) > 0.01f)
            {
                throw new ArgumentException($"Replay artifact header is incompatible for {entry.scenarioId}.");
            }

            float[] continuous = new float[frameCount * ContinuousActionCount];
            ushort[] fireMasks = new ushort[frameCount];
            byte[] specialActions = new byte[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * ContinuousActionCount;
                for (int action = 0; action < encodedContinuousActionCount; action++)
                {
                    float value = reader.ReadSingle();
                    if (!IsFinite(value) || Mathf.Abs(value) > 1.0001f)
                    {
                        throw new ArgumentException(
                            $"Replay artifact contains an invalid continuous action for {entry.scenarioId}.");
                    }

                    // BEESRPL1/2 encoded movement plus sixteen weapon aim pairs. ABI v18 uses only
                    // the first five weapon slots and reserves actions 12-15 for live communication,
                    // so never reinterpret legacy weapon aim values as communication.
                    if (!legacyActionLayout || action < RlOneVsOneAgent.CommunicationContinuousActionStart)
                    {
                        if (action < ContinuousActionCount)
                        {
                            continuous[offset + action] = value;
                        }
                    }
                }
                fireMasks[frame] = reader.ReadUInt16();
                byte specialAction = capabilityAware ? reader.ReadByte() : (byte)RlOneVsOneAgent.NoSpecialAction;
                if (specialAction >= RlOneVsOneAgent.SpecialActionBranchSize)
                {
                    throw new ArgumentException(
                        $"Replay artifact contains an invalid capability action for {entry.scenarioId}.");
                }
                specialActions[frame] = specialAction;
            }
            if (stream.Position != stream.Length)
            {
                throw new ArgumentException($"Replay artifact has unexpected trailing bytes for {entry.scenarioId}.");
            }
            return new ReplayData(
                entry.scenarioId,
                entry.side,
                entry.replayId,
                frameCount,
                fixedStepInterval,
                sourceDirection,
                continuous,
                fireMasks,
                specialActions);
        }
    }

    private static string ReadCatalogPath(string[] args)
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
            if (argument.Equals(CatalogFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                {
                    throw new ArgumentException($"{CatalogFlag} requires a path.");
                }
                candidate = args[++i];
            }
            else
            {
                string prefix = CatalogFlag + "=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = argument.Substring(prefix.Length);
                }
            }
            if (candidate == null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(candidate) || value != null)
            {
                throw new ArgumentException($"{CatalogFlag} may be specified exactly once with a path.");
            }
            value = candidate;
        }
        return value;
    }

    private static bool IsContentId(string value, string prefix, int hexLength)
    {
        return value != null && value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.Length == prefix.Length + hexLength &&
               IsLowerHex(value.Substring(prefix.Length), hexLength);
    }

    private static bool IsLowerHex(string value, int expectedLength)
    {
        if (value == null || value.Length != expectedLength)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] digest = sha.ComputeHash(bytes);
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++)
            {
                builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

/// <summary>
/// Per-Level owner for one scripted replay episode. It never creates policy observations or rewards.
/// It only applies already-validated movement/aim/fire/capability commands to the replay-side ship.
/// </summary>
[DefaultExecutionOrder(20000)]
internal sealed class RlPlayerDerivedActionReplayController : MonoBehaviour
{
    private const float MiningActionIntervalSeconds = 5f;
    private const float HealingActionIntervalSeconds = 1f;
    private const int HealingPerSuccessfulAction = 50;

    private readonly Vector2[] _aimDirections = new Vector2[RlPlayerDerivedActionReplay.WeaponFireBranchCount];

    private Level _level;
    private RlPlayerDerivedActionReplay.ReplayData _replay;
    private Ship _ship;
    private long _boundShipId;
    private int _fixedStepCounter;
    private int _frameIndex;
    private bool _neutralized;
    private bool _hasBoundOnce;
    private float _rotationCos = 1f;
    private float _rotationSin;
    private float _nextMiningActionTime;
    private float _nextHealingActionTime;

    internal void Prepare(Level level, RlPlayerDerivedActionReplay.ReplayData replay)
    {
        ReleaseShip();
        _level = level;
        _replay = replay;
        _fixedStepCounter = 0;
        _frameIndex = 0;
        _neutralized = false;
        _hasBoundOnce = false;
        _rotationCos = 1f;
        _rotationSin = 0f;
        _nextMiningActionTime = 0f;
        _nextHealingActionTime = 0f;
        for (int slot = 0; slot < _aimDirections.Length; slot++)
        {
            _aimDirections[slot] = Vector2.up;
        }
    }

    internal void EndReplayEpisode()
    {
        Neutralize();
        ReleaseShip();
        _replay = null;
        _fixedStepCounter = 0;
        _frameIndex = 0;
        _hasBoundOnce = false;
        _nextMiningActionTime = 0f;
        _nextHealingActionTime = 0f;
    }

    private void FixedUpdate()
    {
        if (_replay == null || _level == null || _level.Stage == null ||
            !RlOneVsOneTrainingBootstrap.IsActiveFor(_level.Stage) || !TryBindShip())
        {
            return;
        }

        _fixedStepCounter++;
        if (_fixedStepCounter < _replay.FixedStepInterval)
        {
            return;
        }
        _fixedStepCounter = 0;

        if (_frameIndex >= _replay.FrameCount)
        {
            Neutralize();
            return;
        }

        ApplyFrame(_frameIndex);
        _frameIndex++;
    }

    private bool TryBindShip()
    {
        if (_ship != null)
        {
            if (!_ship.IsDead && _ship.Level == _level && _ship.Id == _boundShipId)
            {
                return true;
            }
            ReleaseShip();
            _neutralized = true;
            return false;
        }
        if (_hasBoundOnce)
        {
            return false;
        }
        if (_level.State == null || ConfigData.Configuration == null)
        {
            return false;
        }

        int side = _replay.Side == "Bee"
            ? ConfigData.Configuration.BeeSide
            : ConfigData.Configuration.HumanSide;
        List<Ship> ships = _level.State.GetShips(side);
        Ship selected = null;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (!RlOneVsOneAgent.RequiresPolicyControl(candidate))
            {
                continue;
            }
            if (selected == null || CompareShips(candidate, selected) < 0)
            {
                selected = candidate;
            }
        }
        if (selected == null)
        {
            return false;
        }

        _ship = selected;
        _boundShipId = selected.Id;
        _hasBoundOnce = true;
        if (_ship.Squad != null)
        {
            _ship.Squad.IsUserControlled = false;
            _ship.Squad.IsHiveMindControlled = true;
            _ship.Squad.CanAcceptUserInput = false;
        }
        _ship.IsRlPolicyControlled = true;
        ConfigureRotationToCurrentSpawn();
        Vector2 defaultAim = Rotate(Vector2.up);
        for (int slot = 0; slot < _aimDirections.Length; slot++)
        {
            _aimDirections[slot] = defaultAim;
        }
        for (int turretIndex = 0; turretIndex < _ship.Turrets.Count; turretIndex++)
        {
            Turret turret = _ship.Turrets[turretIndex];
            turret.SetRlControl(
                turret.GetPosition() + defaultAim * Mathf.Max(1f, turret.Range),
                false);
        }
        return true;
    }

    private void ConfigureRotationToCurrentSpawn()
    {
        Vector2 current = _ship.GetPosition();
        if (current.sqrMagnitude <= 0.000001f)
        {
            throw new InvalidOperationException(
                $"Replay {_replay.ReplayId} cannot align to a ship spawned at map center.");
        }
        current.Normalize();
        Vector2 source = _replay.SourceStartDirection;
        _rotationCos = Mathf.Clamp(Vector2.Dot(source, current), -1f, 1f);
        _rotationSin = source.x * current.y - source.y * current.x;
    }

    private void ApplyFrame(int frameIndex)
    {
        int offset = frameIndex * RlPlayerDerivedActionReplay.ContinuousActionCount;
        Vector2 movement = Rotate(new Vector2(
            _replay.ContinuousActions[offset],
            _replay.ContinuousActions[offset + 1]));
        RlOneVsOneAgent.ApplyMovementCommand(_ship, movement);

        ushort fireMask = _replay.FireMasks[frameIndex];
        for (int slot = 0; slot < RlPlayerDerivedActionReplay.WeaponFireBranchCount; slot++)
        {
            int aimOffset = offset + RlOneVsOneAgent.WeaponAimContinuousActionStart +
                            slot * RlOneVsOneAgent.WeaponAimContinuousActionsPerSlot;
            Vector2 aim = new Vector2(
                _replay.ContinuousActions[aimOffset],
                _replay.ContinuousActions[aimOffset + 1]);
            if (aim.sqrMagnitude >= RlOneVsOneAgent.AimDeadZone * RlOneVsOneAgent.AimDeadZone)
            {
                _aimDirections[slot] = Rotate(aim.normalized);
            }
            bool fire = (fireMask & (1 << slot)) != 0;
            RlOneVsOneAgent.ApplyWeaponCommand(_ship, slot, _aimDirections[slot], fire);
        }

        ApplyCapabilityAction(_replay.SpecialActions[frameIndex]);
    }

    private void ApplyCapabilityAction(int action)
    {
        switch (action)
        {
            case RlOneVsOneAgent.NoSpecialAction:
                return;
            case RlOneVsOneAgent.ShipSpecialAction:
                ApplyShipSpecialAction();
                return;
            case RlOneVsOneAgent.MiningAction:
                TryApplyMiningAction();
                return;
            case RlOneVsOneAgent.HealingAction:
                TryApplyHealingAction();
                return;
            case RlOneVsOneAgent.WarpAction:
                TryApplyWarpAction();
                return;
            default:
                throw new InvalidOperationException(
                    $"Replay {_replay.ReplayId} contains unsupported capability action {action}.");
        }
    }

    private void ApplyShipSpecialAction()
    {
        if (_ship is YellowJacket yellowJacket)
        {
            yellowJacket.TryToDetonate();
        }
        else if (_ship is Striker striker)
        {
            striker.TryToDropBombs();
        }
        else if (_ship is FireBarge fireBarge)
        {
            fireBarge.Detonate();
        }
        else if (_ship is Barge barge && !barge.HasStartedCharging && !barge.IsCharging)
        {
            barge.StartCoroutine(barge.ChargeForward(FindNearestVisibleEnemy()));
        }
        else if (_ship is Scout scout)
        {
            scout.DropBeacon();
        }
    }

    private void TryApplyMiningAction()
    {
        if (!RlOneVsOneAgent.CanUseMiningAction(_ship) || Time.time < _nextMiningActionTime ||
            _ship.Level == null || _ship.Level.State == null || _ship.Collider == null || _ship.FleetShip == null)
        {
            return;
        }

        MiningAsteroid asteroid = FindTouchingMiningAsteroid();
        if (asteroid == null)
        {
            return;
        }

        int amountMined = Mathf.Min(ConfigData.MiningRate, asteroid.Health);
        if (amountMined <= 0)
        {
            return;
        }

        _nextMiningActionTime = Time.time + MiningActionIntervalSeconds;
        asteroid.Health -= amountMined;
        _ship.FleetShip.MineralsMinedThisLevel += amountMined;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        if (asteroid.Health <= 0 && !asteroid.IsDead)
        {
            asteroid.Kill(false);
        }
    }

    private MiningAsteroid FindTouchingMiningAsteroid()
    {
        MiningAsteroid selected = null;
        foreach (MiningAsteroid asteroid in _ship.Level.State.MiningAsteroids)
        {
            if (asteroid == null || asteroid.IsDead || asteroid.Collider == null ||
                !_ship.Collider.IsTouching(asteroid.Collider))
            {
                continue;
            }
            if (selected == null || asteroid.Id < selected.Id)
            {
                selected = asteroid;
            }
        }
        return selected;
    }

    private void TryApplyHealingAction()
    {
        if (!RlOneVsOneAgent.CanUseHealingAction(_ship) || Time.time < _nextHealingActionTime ||
            _ship.Health >= _ship.MaxHealth || _ship.Level == null || _ship.Level.State == null ||
            _ship.Collider == null || _ship.FleetShip == null)
        {
            return;
        }

        Beehive beehive = FindTouchingBeehive();
        if (beehive == null)
        {
            return;
        }

        int amountHealed = Mathf.Min(HealingPerSuccessfulAction, _ship.MaxHealth - _ship.Health);
        if (amountHealed <= 0)
        {
            return;
        }

        _nextHealingActionTime = Time.time + HealingActionIntervalSeconds;
        _ship.Health += amountHealed;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        _ship.UpdateHealthBar();
        if (_ship.Level.HasPlayer)
        {
            beehive.SpawnHealingCross();
        }
    }

    private Beehive FindTouchingBeehive()
    {
        Beehive selected = null;
        List<Ship> allies = _ship.Level.State.GetShips(_ship.Side);
        for (int i = 0; i < allies.Count; i++)
        {
            if (!(allies[i] is Beehive beehive) || beehive.IsDead || beehive.HealCollider == null ||
                !beehive.HealCollider.IsTouching(_ship.Collider))
            {
                continue;
            }
            if (selected == null || beehive.Id < selected.Id)
            {
                selected = beehive;
            }
        }
        return selected;
    }

    private void TryApplyWarpAction()
    {
        if (!RlOneVsOneAgent.CanUseWarpAction(_ship) || _ship.Level == null || _ship.Level.State == null ||
            _ship.Collider == null)
        {
            return;
        }

        WarpGate warpGate = FindTouchingWarpGate();
        if (warpGate == null)
        {
            return;
        }
        if (warpGate.IsUserControlled && warpGate.EnteringWarpGateSound != null)
        {
            warpGate.EnteringWarpGateSound.Play();
        }
        _ship.EndKill();
    }

    private WarpGate FindTouchingWarpGate()
    {
        WarpGate selected = null;
        List<Ship> allies = _ship.Level.State.GetShips(_ship.Side);
        for (int i = 0; i < allies.Count; i++)
        {
            if (!(allies[i] is WarpGate warpGate) || warpGate.IsDead || warpGate.WarpCollider == null ||
                !warpGate.WarpCollider.IsTouching(_ship.Collider))
            {
                continue;
            }
            if (selected == null || warpGate.Id < selected.Id)
            {
                selected = warpGate;
            }
        }
        return selected;
    }

    private Ship FindNearestVisibleEnemy()
    {
        if (_ship == null || _ship.Level == null || _ship.Level.State == null)
        {
            return null;
        }

        Ship selected = null;
        float selectedDistance = float.MaxValue;
        Vector2 origin = _ship.GetPosition();
        foreach (Ship candidate in _ship.Level.State.GetShipsVisibleToHiveMind(_ship.Side))
        {
            if (candidate == null || candidate.IsDead || candidate.Side == _ship.Side)
            {
                continue;
            }

            float distance = (candidate.GetPosition() - origin).sqrMagnitude;
            if (selected == null || distance < selectedDistance ||
                (Mathf.Approximately(distance, selectedDistance) && candidate.Id < selected.Id))
            {
                selected = candidate;
                selectedDistance = distance;
            }
        }
        return selected;
    }

    private void Neutralize()
    {
        if (_neutralized || _ship == null)
        {
            return;
        }
        _neutralized = true;
        RlOneVsOneAgent.ApplyMovementCommand(_ship, Vector2.zero);
        for (int slot = 0; slot < RlPlayerDerivedActionReplay.WeaponFireBranchCount; slot++)
        {
            RlOneVsOneAgent.ApplyWeaponCommand(_ship, slot, _aimDirections[slot], false);
        }
    }

    private Vector2 Rotate(Vector2 value)
    {
        return new Vector2(
            value.x * _rotationCos - value.y * _rotationSin,
            value.x * _rotationSin + value.y * _rotationCos);
    }

    private static int CompareShips(Ship left, Ship right)
    {
        long leftFleet = left != null && left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
        long rightFleet = right != null && right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
        int compare = leftFleet.CompareTo(rightFleet);
        return compare != 0 ? compare : left.Id.CompareTo(right.Id);
    }

    private void ReleaseShip()
    {
        if (_ship != null)
        {
            _ship.IsRlPolicyControlled = false;
            for (int i = 0; i < _ship.Turrets.Count; i++)
            {
                _ship.Turrets[i].ClearRlControl();
            }
        }
        _ship = null;
        _boundShipId = 0;
    }
}