using Assets.Scripts;
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
/// scenarios. The replay side is scripted movement/turret aim/fire only; the opposing policy still
/// produces fresh PPO actions. Recorded world-space actions are rotated from the source spawn axis
/// to the current randomized training spawn axis so arena rotation remains useful augmentation.
/// </summary>
internal static class RlPlayerDerivedActionReplay
{
    internal const string CatalogFlag = "--bees-adversarial-replay-catalog";
    internal const int CatalogSchemaVersion = 1;
    internal const int ContinuousActionCount = 34;
    internal const int WeaponFireBranchCount = 16;
    internal const int MaximumReplayFrames = 2400;
    internal const int ExpectedFixedStepInterval = 5;

    private const string ReplayMagic = "BEESRPL1";
    private const string PlayerDerivedTagPrefix = "player-derived:";
    private const int ReplayHeaderBytes = 24;
    private const int ReplayFrameBytes = ContinuousActionCount * sizeof(float) + sizeof(ushort);

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

        internal ReplayData(
            string scenarioId,
            string side,
            string replayId,
            int frameCount,
            int fixedStepInterval,
            Vector2 sourceStartDirection,
            float[] continuousActions,
            ushort[] fireMasks)
        {
            ScenarioId = scenarioId;
            Side = side;
            ReplayId = replayId;
            FrameCount = frameCount;
            FixedStepInterval = fixedStepInterval;
            SourceStartDirection = sourceStartDirection;
            ContinuousActions = continuousActions;
            FireMasks = fireMasks;
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

    private static ReplayData ReadReplay(ReplayCatalogEntry entry, string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Replay artifact does not exist for {entry.scenarioId}: {path}");
        }
        long expectedLength = ReplayHeaderBytes + (long)entry.frameCount * ReplayFrameBytes;
        long actualLength = new FileInfo(path).Length;
        if (actualLength != expectedLength)
        {
            throw new ArgumentException(
                $"Replay artifact size mismatch for {entry.scenarioId}: expected {expectedLength}, got {actualLength}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string actualHash = ComputeSha256(bytes);
        if (!actualHash.Equals(entry.replaySha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Replay artifact hash mismatch for {entry.scenarioId}: expected {entry.replaySha256}, got {actualHash}.");
        }

        using (MemoryStream stream = new MemoryStream(bytes, false))
        using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII, false))
        {
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
            int frameCount = reader.ReadInt32();
            int fixedStepInterval = reader.ReadInt32();
            Vector2 sourceDirection = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            if (magic != ReplayMagic || frameCount != entry.frameCount ||
                fixedStepInterval != entry.fixedStepInterval ||
                !IsFinite(sourceDirection.x) || !IsFinite(sourceDirection.y) ||
                Mathf.Abs(sourceDirection.magnitude - 1f) > 0.01f)
            {
                throw new ArgumentException($"Replay artifact header is incompatible for {entry.scenarioId}.");
            }

            float[] continuous = new float[frameCount * ContinuousActionCount];
            ushort[] fireMasks = new ushort[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * ContinuousActionCount;
                for (int action = 0; action < ContinuousActionCount; action++)
                {
                    float value = reader.ReadSingle();
                    if (!IsFinite(value) || Mathf.Abs(value) > 1.0001f)
                    {
                        throw new ArgumentException(
                            $"Replay artifact contains an invalid continuous action for {entry.scenarioId}.");
                    }
                    continuous[offset + action] = value;
                }
                fireMasks[frame] = reader.ReadUInt16();
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
                fireMasks);
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
/// It only applies already-validated movement/aim/fire commands to the selected replay-side ship.
/// </summary>
[DefaultExecutionOrder(20000)]
internal sealed class RlPlayerDerivedActionReplayController : MonoBehaviour
{
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
        _ship.HasBrain = true;
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
            _ship.HasBrain = false;
            for (int i = 0; i < _ship.Turrets.Count; i++)
            {
                _ship.Turrets[i].ClearRlControl();
            }
        }
        _ship = null;
        _boundShipId = 0;
    }
}
