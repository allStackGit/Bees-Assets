using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Demonstrations;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Passive demonstration adapter for ordinary gameplay. It observes the final ship control state
/// produced by a human player or the Hive Mind and records that state using the same observation and
/// action ABI as the shared Bees combat policy. OnActionReceived is deliberately inert: this adapter
/// never owns gameplay and can therefore be removed or fail without changing ship behavior.
///
/// Capture is explicit opt-in via --rl-record-demonstrations. The resulting ML-Agents .demo files
/// are written below Application.persistentDataPath/RlDemonstrations, partitioned by frozen policy
/// ABI and then by Human/HiveMind source. They remain separate from PPO rollouts so a central
/// trainer/uploader can consume them through an imitation-learning path.
/// </summary>
internal sealed class RlGameplayDemonstrationAgent : Agent
{
    internal const string CaptureCommandLineFlag = "--rl-record-demonstrations";
    internal const string DemonstrationDirectoryName = "RlDemonstrations";
    internal const string CaptureManifestFileName = "capture-manifest.json";
    private const int CaptureManifestSchemaVersion = 1;

    private enum DemonstrationSource
    {
        None = 0,
        Human = 1,
        HiveMind = 2
    }

    [Serializable]
    private sealed class DemonstrationCaptureManifest
    {
        public int schemaVersion;
        public string behaviorName;
        public int policyAbiVersion;
        public string policySignature;
        public int observationSize;
        public int continuousActionCount;
        public int[] discreteBranchSizes;
    }

    private static readonly List<RlGameplayDemonstrationAgent> Instances =
        new List<RlGameplayDemonstrationAgent>();
    private static int _lastProvisionFrame = -1;

    private Stage _stage;
    private Level _level;
    private Ship _ship;
    private int _side;
    private DemonstrationSource _source;
    private int _decisionCounter;
    private bool _hasBoundShip;
    private long _boundRuntimeShipId;
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly RlCombatPerception _perception = new RlCombatPerception();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForPlayerFacingStage()
    {
        if (RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            !IsCaptureRequested(Environment.GetCommandLineArgs()))
        {
            return;
        }

        Stage stage = UnityEngine.Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            return;
        }

        stage.StartCoroutine(InstallWhenStageIsReady(stage));
    }

    private static IEnumerator InstallWhenStageIsReady(Stage stage)
    {
        while (stage != null && (!stage.IsFinalized || ConfigData.Configuration == null))
        {
            yield return null;
        }

        if (stage == null || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            !IsCaptureRequested(Environment.GetCommandLineArgs()))
        {
            yield break;
        }

        RlPolicySchema.ValidateOrThrow();
        if (!TryEnsureCaptureManifest(out string manifestError))
        {
            Debug.LogError($"Passive RL demonstration capture disabled: {manifestError}");
            yield break;
        }

        _lastProvisionFrame = -1;
        ProvisionAgentsForSpawnedShips(stage, true);

        Debug.Log($"Passive RL demonstration capture enabled: ABI v{RlPolicySchema.Version} " +
                  $"{RlPolicySchema.Signature}; output={GetDemonstrationRootDirectory()}.");
    }

    internal static bool IsCaptureRequested(IReadOnlyList<string> args)
    {
        if (args == null)
        {
            return false;
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], CaptureCommandLineFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static Vector2 EncodeMovementDirection(int direction)
    {
        // Ship.Direction == 360 is the existing stop sentinel. RlOneVsOneAgent converts an action
        // vector into degrees with -atan2(x, y), so this is the exact inverse for unit movement.
        if (direction < 0 || direction >= 360)
        {
            return Vector2.zero;
        }

        float radians = direction * Mathf.Deg2Rad;
        return new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
    }

    internal static int DetermineSourceForTests(bool isUserControlled, bool isHiveMindControlled, bool isLiveRlControlled)
    {
        return (int)DetermineSource(isUserControlled, isHiveMindControlled, isLiveRlControlled);
    }

    internal static string GetSourceDirectoryNameForTests(int source)
    {
        return GetSourceDirectoryName((DemonstrationSource)source);
    }

    internal static string GetPolicyDirectoryNameForTests()
    {
        return GetPolicyDirectoryName();
    }

    private static DemonstrationSource DetermineSource(
        bool isUserControlled,
        bool isHiveMindControlled,
        bool isLiveRlControlled)
    {
        if (isLiveRlControlled)
        {
            return DemonstrationSource.None;
        }
        if (isUserControlled)
        {
            return DemonstrationSource.Human;
        }
        return isHiveMindControlled ? DemonstrationSource.HiveMind : DemonstrationSource.None;
    }

    private static string GetSourceDirectoryName(DemonstrationSource source)
    {
        switch (source)
        {
            case DemonstrationSource.Human:
                return "Human";
            case DemonstrationSource.HiveMind:
                return "HiveMind";
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Only live human and Hive Mind demonstrations may be recorded.");
        }
    }

    private static string GetPolicyDirectoryName()
    {
        return $"PolicyV{RlPolicySchema.Version}";
    }

    private static string GetDemonstrationRootDirectory()
    {
        return Path.Combine(
            Application.persistentDataPath,
            DemonstrationDirectoryName,
            GetPolicyDirectoryName());
    }

    private static string GetDemonstrationDirectory(DemonstrationSource source)
    {
        return Path.Combine(GetDemonstrationRootDirectory(), GetSourceDirectoryName(source));
    }

    private static DemonstrationCaptureManifest CreateCaptureManifest()
    {
        return new DemonstrationCaptureManifest
        {
            schemaVersion = CaptureManifestSchemaVersion,
            behaviorName = RlOneVsOneAgent.BehaviorName,
            policyAbiVersion = RlPolicySchema.Version,
            policySignature = RlPolicySchema.Signature,
            observationSize = RlOneVsOneAgent.ObservationSize,
            continuousActionCount = RlOneVsOneAgent.ContinuousActionCount,
            discreteBranchSizes = RlOneVsOneAgent.CreateDiscreteBranchSizes()
        };
    }

    private static bool TryEnsureCaptureManifest(out string error)
    {
        string root = GetDemonstrationRootDirectory();
        string path = Path.Combine(root, CaptureManifestFileName);
        DemonstrationCaptureManifest expected = CreateCaptureManifest();
        try
        {
            Directory.CreateDirectory(root);
            if (File.Exists(path))
            {
                DemonstrationCaptureManifest existing =
                    JsonUtility.FromJson<DemonstrationCaptureManifest>(File.ReadAllText(path));
                if (!CaptureManifestsMatch(existing, expected))
                {
                    error = $"capture manifest at {path} does not match policy ABI v{RlPolicySchema.Version}; " +
                            "move or remove the incompatible capture directory before recording.";
                    return false;
                }
                error = null;
                return true;
            }

            string[] existingDemos = Directory.GetFiles(root, "*.demo", SearchOption.AllDirectories);
            if (existingDemos.Length > 0)
            {
                error = $"capture directory {root} already contains demonstrations but no ABI manifest; " +
                        "move the legacy files before recording so they are not silently relabeled.";
                return false;
            }

            File.WriteAllText(path, JsonUtility.ToJson(expected, true));
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = $"could not validate demonstration capture metadata at {path}: " +
                    $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool CaptureManifestsMatch(
        DemonstrationCaptureManifest left,
        DemonstrationCaptureManifest right)
    {
        if (left == null || right == null ||
            left.schemaVersion != right.schemaVersion ||
            !string.Equals(left.behaviorName, right.behaviorName, StringComparison.Ordinal) ||
            left.policyAbiVersion != right.policyAbiVersion ||
            !string.Equals(left.policySignature, right.policySignature, StringComparison.Ordinal) ||
            left.observationSize != right.observationSize ||
            left.continuousActionCount != right.continuousActionCount)
        {
            return false;
        }

        if (left.discreteBranchSizes == null || right.discreteBranchSizes == null ||
            left.discreteBranchSizes.Length != right.discreteBranchSizes.Length)
        {
            return false;
        }
        for (int i = 0; i < left.discreteBranchSizes.Length; i++)
        {
            if (left.discreteBranchSizes[i] != right.discreteBranchSizes[i])
            {
                return false;
            }
        }
        return true;
    }

    private static void ProvisionAgentsForSpawnedShips(Stage stage, bool force = false)
    {
        if (stage == null || ConfigData.Configuration == null ||
            (!force && (Time.frameCount == _lastProvisionFrame ||
                        Time.frameCount % RlOneVsOneTrainingOptions.DefaultDecisionPeriod != 0)))
        {
            return;
        }

        _lastProvisionFrame = Time.frameCount;
        IReadOnlyList<Level> levels = stage.Levels;
        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            Level level = levels[levelIndex];
            if (level == null || level.State == null)
            {
                continue;
            }

            EnsureAgentCount(stage, level, ConfigData.Configuration.BeeSide, DemonstrationSource.Human, levelIndex);
            EnsureAgentCount(stage, level, ConfigData.Configuration.BeeSide, DemonstrationSource.HiveMind, levelIndex);
            EnsureAgentCount(stage, level, ConfigData.Configuration.HumanSide, DemonstrationSource.Human, levelIndex);
            EnsureAgentCount(stage, level, ConfigData.Configuration.HumanSide, DemonstrationSource.HiveMind, levelIndex);
        }
    }

    private static void EnsureAgentCount(
        Stage stage,
        Level level,
        int side,
        DemonstrationSource source,
        int levelIndex)
    {
        int required = CountEligibleShips(stage, level, side, source);
        int existing = CountAgents(level, side, source);
        for (int slot = existing; slot < required; slot++)
        {
            CreateAgent(stage, level, side, source, $"Arena {levelIndex} Side {side} Slot {slot}");
        }
    }

    private static int CountEligibleShips(Stage stage, Level level, int side, DemonstrationSource source)
    {
        int count = 0;
        List<Ship> ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            if (IsEligible(stage, level, ships[i], source))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountAgents(Level level, int side, DemonstrationSource source)
    {
        int count = 0;
        for (int i = 0; i < Instances.Count; i++)
        {
            RlGameplayDemonstrationAgent agent = Instances[i];
            if (agent != null && agent._level == level && agent._side == side && agent._source == source)
            {
                count++;
            }
        }
        return count;
    }

    private static void CreateAgent(
        Stage stage,
        Level level,
        int side,
        DemonstrationSource source,
        string label)
    {
        GameObject obj = new GameObject($"RL Demonstration Agent - {source} - {label}");
        obj.transform.SetParent(level != null ? level.transform : stage.transform, false);

        BehaviorParameters behavior = obj.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = RlOneVsOneAgent.BehaviorName;
        behavior.TeamId = 0;
        behavior.BehaviorType = BehaviorType.HeuristicOnly;
        behavior.BrainParameters.VectorObservationSize = RlOneVsOneAgent.ObservationSize;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = new ActionSpec(
            RlOneVsOneAgent.ContinuousActionCount,
            RlOneVsOneAgent.CreateDiscreteBranchSizes());

        RlGameplayDemonstrationAgent agent = obj.AddComponent<RlGameplayDemonstrationAgent>();
        agent._stage = stage;
        agent._level = level;
        agent._side = side;
        agent._source = source;

        DemonstrationRecorder recorder = obj.AddComponent<DemonstrationRecorder>();
        recorder.DemonstrationName = source == DemonstrationSource.Human
            ? $"human-s{side}"
            : $"hivemind-s{side}";
        recorder.DemonstrationDirectory = GetDemonstrationDirectory(source);
        recorder.NumStepsToRecord = 0;
        recorder.Record = true;
    }

    public override void Initialize()
    {
        Instances.Add(this);
    }

    protected override void OnDisable()
    {
        Instances.Remove(this);
        ReleaseShip();
        base.OnDisable();
    }

    private void FixedUpdate()
    {
        // The command line cannot change after startup, so do not allocate/scan it on every physics tick.
        if (_stage == null || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        ProvisionAgentsForSpawnedShips(_stage);
        if (!TryBindShip())
        {
            return;
        }

        _decisionCounter++;
        if (_decisionCounter >= RlOneVsOneTrainingOptions.DefaultDecisionPeriod)
        {
            _decisionCounter = 0;
            RequestDecision();
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (!TryBindShip())
        {
            for (int i = 0; i < RlOneVsOneAgent.ObservationSize; i++)
            {
                sensor.AddObservation(0f);
            }
            return;
        }

        _perception.Collect(_ship, _side, sensor, 0);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<float> continuous = actionsOut.ContinuousActions;
        for (int i = 0; i < continuous.Length; i++)
        {
            continuous[i] = 0f;
        }

        ActionSegment<int> discrete = actionsOut.DiscreteActions;
        for (int i = 0; i < discrete.Length; i++)
        {
            discrete[i] = 0;
        }

        if (!TryBindShip())
        {
            return;
        }

        Vector2 movement = EncodeMovementDirection(_ship.Direction);
        continuous[0] = movement.x;
        continuous[1] = movement.y;

        for (int slot = 0; slot < RlOneVsOneAgent.MaxWeaponSlots; slot++)
        {
            if (_ship.Weapons == null || slot >= _ship.Weapons.Count || !(_ship.Weapons[slot] is Turret turret))
            {
                continue;
            }

            int aimStart = RlOneVsOneAgent.WeaponAimContinuousActionStart +
                           slot * RlOneVsOneAgent.WeaponAimContinuousActionsPerSlot;
            Vector2 aim = turret.TargetPoint - turret.GetPosition();
            if (aim.sqrMagnitude > 0.0001f)
            {
                aim.Normalize();
                continuous[aimStart] = aim.x;
                continuous[aimStart + 1] = aim.y;
            }

            bool fireRequested = turret.IsFiringManually || turret.ShouldFire || turret.ShouldFireAtAsteroid;
            discrete[RlOneVsOneAgent.WeaponFireBranchStart + slot] = fireRequested
                ? RlOneVsOneAgent.FireWeaponAction
                : RlOneVsOneAgent.CeaseWeaponAction;
        }

        // One-shot ship/mining/healing/warp actions are not inferred from aftermath. Those need
        // explicit authoritative event hooks so demonstrations never invent an action that did not
        // occur. Until those hooks are added, the special branch remains NoSpecialAction.
        discrete[RlOneVsOneAgent.SpecialActionBranch] = RlOneVsOneAgent.NoSpecialAction;
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Observation-only by contract. Never apply recorded actions back to the live ship.
    }

    private bool TryBindShip()
    {
        if (_level == null || _level.State == null)
        {
            ReleaseShip();
            return false;
        }

        if (_hasBoundShip)
        {
            if (_ship != null && !_ship.IsDead && _ship.Level == _level &&
                _ship.Id == _boundRuntimeShipId && IsEligible(_stage, _level, _ship, _source))
            {
                return true;
            }
            ReleaseShip();
        }

        _bindCandidates.Clear();
        List<Ship> ships = _level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (IsEligible(_stage, _level, candidate, _source) && !IsObservedByAnotherAgent(candidate))
            {
                _bindCandidates.Add(candidate);
            }
        }
        if (_bindCandidates.Count == 0)
        {
            return false;
        }

        _bindCandidates.Sort(CompareShipsForCapture);
        _ship = _bindCandidates[0];
        if (!RlPolicySchema.TryValidateShip(_ship, out string schemaError))
        {
            Debug.LogError(schemaError);
            _ship = null;
            return false;
        }

        _boundRuntimeShipId = _ship.Id;
        _hasBoundShip = true;
        _decisionCounter = 0;
        return true;
    }

    private static bool IsEligible(
        Stage stage,
        Level level,
        Ship ship,
        DemonstrationSource expectedSource)
    {
        if (ship == null || ship.IsDead || ship.Level != level || ship.Squad == null ||
            !RlOneVsOneAgent.RequiresPolicyControl(ship))
        {
            return false;
        }

        // The historical squad flag can remain true even when Stage has disabled the Hive Mind.
        // Do not turn that stale ownership metadata into a training demonstration.
        if (expectedSource == DemonstrationSource.HiveMind &&
            (stage == null || !stage.ActivateHiveMind || stage.ActivateBrains))
        {
            return false;
        }

        bool liveRlControlled = IsLiveRlControlled(stage, level, ship);
        DemonstrationSource actualSource = DetermineSource(
            ship.Squad.IsUserControlled,
            ship.Squad.IsHiveMindControlled,
            liveRlControlled);
        return actualSource == expectedSource;
    }

    private static bool IsLiveRlControlled(Stage stage, Level level, Ship ship)
    {
        if (stage == null || level == null || ship == null || ConfigData.Configuration == null ||
            !stage.ActivateHiveMind || !stage.ActivateBrains)
        {
            return false;
        }

        return RlLivePolicyAgent.ShouldControlSide(level.HasPlayer, ship.Side, ConfigData.Configuration.AISide);
    }

    private bool IsObservedByAnotherAgent(Ship candidate)
    {
        for (int i = 0; i < Instances.Count; i++)
        {
            RlGameplayDemonstrationAgent other = Instances[i];
            if (other != null && other != this && other._level == _level && other._side == _side &&
                other._source == _source && other._hasBoundShip && other._ship == candidate)
            {
                return true;
            }
        }
        return false;
    }

    private static int CompareShipsForCapture(Ship left, Ship right)
    {
        long leftFleetId = left != null && left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
        long rightFleetId = right != null && right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
        int compare = leftFleetId.CompareTo(rightFleetId);
        return compare != 0 ? compare : left.Id.CompareTo(right.Id);
    }

    private void ReleaseShip()
    {
        _ship = null;
        _hasBoundShip = false;
        _boundRuntimeShipId = 0;
        _decisionCounter = 0;
    }
}
