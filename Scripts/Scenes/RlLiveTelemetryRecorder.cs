using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Automatically records player decisions made while a Production player-facing Stage is using the
/// approved RL champion. Telemetry is passive: it never owns gameplay, rewards, or policy updates.
/// Completed bounded segments are persisted below Application.persistentDataPath and are uploaded by
/// RlLiveTelemetryUploader. One-shot capability events may call RecordCapability synchronously so
/// they are represented as the action that actually occurred rather than inferred from aftermath.
/// </summary>
internal sealed class RlLiveTelemetryRecorder : MonoBehaviour
{
    internal const string DisableCommandLineFlag = "--rl-disable-automatic-telemetry";
    internal const string TelemetryDirectoryName = "RlLiveTelemetry";
    internal const int MaxStepsPerSegment = 64;

    private const string ModeName = "player-live-rl";
    private static readonly FieldInfo VectorObservationsField = typeof(VectorSensor).GetField(
        "m_Observations", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo BootstrapDeploymentField = typeof(RlLivePolicyModelBootstrap).GetField(
        "_deploymentId", BindingFlags.Instance | BindingFlags.NonPublic);
    private static RlLiveTelemetryRecorder _instance;

    [Serializable]
    internal sealed class DeploymentCompatibility
    {
        public string behavior_name;
        public int policy_abi_version;
        public int observation_schema_version;
        public int action_schema_version;
        public int reward_schema_version;
        public int scenario_schema_version;
    }

    [Serializable]
    internal sealed class DeploymentIdentity
    {
        public string model_id;
        public string model_sha256;
        public string behavior_name;
        public string policy_signature;
        public DeploymentCompatibility compatibility;
        public string game_build_version;
    }

    [Serializable]
    internal sealed class DeploymentManifest
    {
        public int schema_version;
        public string deployment_id;
        public DeploymentIdentity identity;
    }

    internal sealed class TelemetryStep
    {
        public string agent_key;
        public int decision_index;
        public float[] observation;
        public float[] continuous_action;
        public int[] discrete_action;
    }

    internal sealed class TelemetryPayload
    {
        public int schema_version = 1;
        public string match_id;
        public string source_match_id;
        public int segment_index;
        public string game_build_version;
        public string mode = ModeName;
        public string result;
        public string model_id;
        public string model_sha256;
        public string deployment_id;
        public string policy_signature;
        public string behavior_name;
        public int policy_abi_version;
        public int observation_schema_version;
        public int action_schema_version;
        public int reward_schema_version;
        public int scenario_schema_version;
        public List<TelemetryStep> steps = new List<TelemetryStep>();
    }

    private sealed class LevelSession
    {
        public Level Level;
        public string SourceMatchId;
        public int SegmentIndex;
        public int DecisionCounter;
        public bool Completed;
        public TelemetryPayload Current;
        public readonly Dictionary<long, int> DecisionByShip = new Dictionary<long, int>();
        public readonly List<string> DraftPaths = new List<string>();
    }

    private readonly Dictionary<Level, LevelSession> _sessions = new Dictionary<Level, LevelSession>();
    private readonly RlCombatPerception _perception = new RlCombatPerception();
    private Stage _stage;
    private DeploymentManifest _cachedManifest;
    private string _cachedDeploymentId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForPlayerFacingStage()
    {
        if (RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            IsDisabled(Environment.GetCommandLineArgs()))
        {
            return;
        }

        Stage stage = UnityEngine.Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            return;
        }
        RlLiveTelemetryRecorder recorder = stage.GetComponent<RlLiveTelemetryRecorder>();
        if (recorder == null)
        {
            recorder = stage.gameObject.AddComponent<RlLiveTelemetryRecorder>();
        }
        recorder._stage = stage;
        _instance = recorder;
        stage.StartCoroutine(recorder.EnableWhenReady());
    }

    private IEnumerator EnableWhenReady()
    {
        while (_stage != null && (!_stage.IsFinalized || ConfigData.Configuration == null))
        {
            yield return null;
        }
#if UNITY_WEBGL
        yield break;
#else
        if (_stage == null || !ConfigData.Production || !IsLiveRlEnabled(_stage))
        {
            yield break;
        }
        try
        {
            RlPolicySchema.ValidateOrThrow();
            Directory.CreateDirectory(GetPendingDirectory());
            Directory.CreateDirectory(GetDraftDirectory());
            RecoverAbandonedDrafts();
            Debug.Log("Automatic live RL player telemetry enabled; completed segments remain quarantined until central validation.");
        }
        catch (Exception exception)
        {
            Debug.LogError("Automatic live RL player telemetry disabled: " + exception.Message);
            enabled = false;
        }
#endif
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void FixedUpdate()
    {
#if UNITY_WEBGL
        return;
#else
        if (!enabled || _stage == null || !ConfigData.Production || !IsLiveRlEnabled(_stage))
        {
            return;
        }
        IReadOnlyList<Level> levels = _stage.Levels;
        for (int i = 0; i < levels.Count; i++)
        {
            Level level = levels[i];
            if (level == null || level.State == null || !level.HasPlayer)
            {
                continue;
            }
            LevelSession session = GetSession(level);
            if (session.Completed)
            {
                continue;
            }
            if (level.State.LevelEnded)
            {
                CompleteSession(session);
                continue;
            }
            session.DecisionCounter++;
            if (session.DecisionCounter < RlOneVsOneTrainingOptions.DefaultDecisionPeriod)
            {
                continue;
            }
            session.DecisionCounter = 0;
            RecordPlayerShips(session, RlOneVsOneAgent.NoSpecialAction, null);
        }
#endif
    }

    internal static void RecordCapability(Ship ship, int specialAction)
    {
#if !UNITY_WEBGL
        if (_instance == null || ship == null || ship.Level == null ||
            specialAction <= RlOneVsOneAgent.NoSpecialAction ||
            specialAction >= RlOneVsOneAgent.SpecialActionBranchSize)
        {
            return;
        }
        _instance.RecordCapabilityInternal(ship, specialAction);
#endif
    }

    private void RecordCapabilityInternal(Ship ship, int specialAction)
    {
        if (!enabled || _stage == null || !ConfigData.Production || !IsLiveRlEnabled(_stage) ||
            ship.Squad == null || !ship.Squad.IsUserControlled || ship.IsDead ||
            ship.Level.State == null || ship.Level.State.LevelEnded)
        {
            return;
        }
        LevelSession session = GetSession(ship.Level);
        if (!session.Completed)
        {
            RecordPlayerShips(session, specialAction, ship);
        }
    }

    private static bool IsDisabled(IReadOnlyList<string> args)
    {
        if (args == null)
        {
            return false;
        }
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], DisableCommandLineFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsLiveRlEnabled(Stage stage)
    {
        return stage != null && stage.ActivateHiveMind && stage.ActivateBrains;
    }

    private LevelSession GetSession(Level level)
    {
        if (_sessions.TryGetValue(level, out LevelSession existing))
        {
            return existing;
        }
        LevelSession created = new LevelSession
        {
            Level = level,
            SourceMatchId = "live-" + Guid.NewGuid().ToString("N")
        };
        _sessions.Add(level, created);
        return created;
    }

    private void RecordPlayerShips(LevelSession session, int specialAction, Ship capabilityShip)
    {
        if (!TryResolveCurrentDeployment(out DeploymentManifest manifest, out string identityError))
        {
            Debug.LogWarning("Skipping live RL telemetry sample because deployment identity is unavailable: " + identityError);
            return;
        }

        List<Ship> ships = session.Level.State.GetShips(ConfigData.Configuration.BeeSide);
        RecordEligibleList(session, ships, manifest, specialAction, capabilityShip);
        ships = session.Level.State.GetShips(ConfigData.Configuration.HumanSide);
        RecordEligibleList(session, ships, manifest, specialAction, capabilityShip);
    }

    private void RecordEligibleList(
        LevelSession session,
        List<Ship> ships,
        DeploymentManifest manifest,
        int specialAction,
        Ship capabilityShip)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship == null || ship.IsDead || ship.Squad == null || !ship.Squad.IsUserControlled ||
                !RlOneVsOneAgent.RequiresPolicyControl(ship))
            {
                continue;
            }
            if (capabilityShip != null && ship != capabilityShip)
            {
                continue;
            }
            if (!RlPolicySchema.TryValidateShip(ship, out string schemaError))
            {
                Debug.LogWarning("Skipping incompatible player telemetry ship: " + schemaError);
                continue;
            }
            if (session.Current != null &&
                !string.Equals(session.Current.deployment_id, manifest.deployment_id, StringComparison.Ordinal))
            {
                FlushDraft(session);
            }
            TelemetryPayload payload = EnsurePayload(session, manifest);
            if (!TryCreateStep(session, ship, specialAction, out TelemetryStep step))
            {
                continue;
            }
            payload.steps.Add(step);
            if (payload.steps.Count >= MaxStepsPerSegment)
            {
                FlushDraft(session);
            }
        }
    }

    private TelemetryPayload EnsurePayload(LevelSession session, DeploymentManifest manifest)
    {
        if (session.Current != null)
        {
            return session.Current;
        }
        DeploymentIdentity identity = manifest.identity;
        session.Current = new TelemetryPayload
        {
            match_id = $"{session.SourceMatchId}-s{session.SegmentIndex:D6}",
            source_match_id = session.SourceMatchId,
            segment_index = session.SegmentIndex,
            game_build_version = identity.game_build_version,
            result = "draw",
            model_id = identity.model_id,
            model_sha256 = identity.model_sha256,
            deployment_id = manifest.deployment_id,
            policy_signature = identity.policy_signature,
            behavior_name = identity.behavior_name,
            policy_abi_version = identity.compatibility.policy_abi_version,
            observation_schema_version = identity.compatibility.observation_schema_version,
            action_schema_version = identity.compatibility.action_schema_version,
            reward_schema_version = identity.compatibility.reward_schema_version,
            scenario_schema_version = identity.compatibility.scenario_schema_version
        };
        return session.Current;
    }

    private bool TryCreateStep(LevelSession session, Ship ship, int specialAction, out TelemetryStep step)
    {
        step = null;
        if (VectorObservationsField == null)
        {
            return false;
        }
        VectorSensor sensor = new VectorSensor(RlPolicySchema.ObservationSize);
        _perception.Collect(ship, ship.Side, sensor, 0);
        if (!(VectorObservationsField.GetValue(sensor) is List<float> observations) ||
            observations.Count != RlPolicySchema.ObservationSize)
        {
            return false;
        }

        float[] continuous = new float[RlOneVsOneAgent.ContinuousActionCount];
        int[] discrete = new int[RlOneVsOneAgent.DiscreteBranchCount];
        Vector2 movement = RlGameplayDemonstrationAgent.EncodeMovementDirection(ship.Direction);
        continuous[0] = movement.x;
        continuous[1] = movement.y;
        for (int slot = 0; slot < RlOneVsOneAgent.MaxWeaponSlots; slot++)
        {
            if (ship.Weapons == null || slot >= ship.Weapons.Count || !(ship.Weapons[slot] is Turret turret))
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
        discrete[RlOneVsOneAgent.SpecialActionBranch] = specialAction;

        int decisionIndex = 0;
        if (session.DecisionByShip.TryGetValue(ship.Id, out int previous))
        {
            decisionIndex = previous + 1;
        }
        session.DecisionByShip[ship.Id] = decisionIndex;
        step = new TelemetryStep
        {
            agent_key = $"player-s{ship.Side}-ship{ship.Id}",
            decision_index = decisionIndex,
            observation = observations.ToArray(),
            continuous_action = continuous,
            discrete_action = discrete
        };
        return true;
    }

    private void FlushDraft(LevelSession session)
    {
        TelemetryPayload payload = session.Current;
        session.Current = null;
        if (payload == null || payload.steps == null || payload.steps.Count == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(GetDraftDirectory());
            string path = Path.Combine(GetDraftDirectory(), payload.match_id + ".draft.json");
            WriteAtomic(path, JsonConvert.SerializeObject(payload, Formatting.None));
            session.DraftPaths.Add(path);
            session.SegmentIndex++;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not persist live RL telemetry draft: " + exception.Message);
        }
    }

    private void CompleteSession(LevelSession session)
    {
        FlushDraft(session);
        string result = ResolveResult(session.Level);
        for (int i = 0; i < session.DraftPaths.Count; i++)
        {
            string draft = session.DraftPaths[i];
            try
            {
                TelemetryPayload payload = JsonConvert.DeserializeObject<TelemetryPayload>(File.ReadAllText(draft));
                if (payload == null || payload.steps == null || payload.steps.Count == 0)
                {
                    File.Delete(draft);
                    continue;
                }
                payload.result = result;
                Directory.CreateDirectory(GetPendingDirectory());
                string pending = Path.Combine(GetPendingDirectory(), payload.match_id + ".json");
                WriteAtomic(pending, JsonConvert.SerializeObject(payload, Formatting.None));
                File.Delete(draft);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not finalize live RL telemetry segment: " + exception.Message);
            }
        }
        session.Completed = true;
    }

    private static string ResolveResult(Level level)
    {
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return "draw";
        }
        int winner = level.State.WinningSide;
        if (winner == ConfigData.Configuration.BeeSide)
        {
            return "bee_win";
        }
        if (winner == ConfigData.Configuration.HumanSide)
        {
            return "human_win";
        }
        return "timeout";
    }

    private bool TryResolveCurrentDeployment(out DeploymentManifest manifest, out string error)
    {
        manifest = null;
        error = null;
        RlLivePolicyModelBootstrap bootstrap = _stage != null
            ? _stage.GetComponent<RlLivePolicyModelBootstrap>()
            : null;
        if (bootstrap == null || BootstrapDeploymentField == null)
        {
            error = "live policy bootstrap identity is unavailable";
            return false;
        }
        string deploymentId = BootstrapDeploymentField.GetValue(bootstrap) as string;
        if (string.IsNullOrEmpty(deploymentId))
        {
            error = "live policy deployment ID is empty";
            return false;
        }
        if (_cachedManifest != null && string.Equals(_cachedDeploymentId, deploymentId, StringComparison.Ordinal))
        {
            manifest = _cachedManifest;
            return true;
        }

        TextAsset bundled = Resources.Load<TextAsset>(RlLivePolicyModelBootstrap.ManifestResourcePath);
        if (bundled != null && TryParseManifest(bundled.text, deploymentId, out manifest))
        {
            CacheManifest(manifest);
            return true;
        }

        string hotPath = Path.Combine(
            Application.persistentDataPath,
            "RlPolicyHotBundles",
            deploymentId + ".bundle");
        if (!File.Exists(hotPath))
        {
            error = "hot deployment manifest bundle is unavailable";
            return false;
        }
        AssetBundle bundle = AssetBundle.LoadFromFile(hotPath);
        if (bundle == null)
        {
            error = "hot deployment bundle could not be loaded for telemetry identity";
            return false;
        }
        try
        {
            TextAsset hotManifest = bundle.LoadAsset<TextAsset>(RlLivePolicyModelUpdater.ManifestAddress);
            if (hotManifest == null || !TryParseManifest(hotManifest.text, deploymentId, out manifest))
            {
                error = "hot deployment manifest does not match the active deployment";
                return false;
            }
            CacheManifest(manifest);
            return true;
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    private static bool TryParseManifest(string json, string expectedDeploymentId, out DeploymentManifest manifest)
    {
        manifest = null;
        try
        {
            manifest = JsonUtility.FromJson<DeploymentManifest>(json);
        }
        catch
        {
            return false;
        }
        if (manifest == null || manifest.schema_version != RlLivePolicyModelBootstrap.DeploymentManifestSchemaVersion ||
            !string.Equals(manifest.deployment_id, expectedDeploymentId, StringComparison.Ordinal) ||
            manifest.identity == null || manifest.identity.compatibility == null ||
            string.IsNullOrEmpty(manifest.identity.model_id) ||
            string.IsNullOrEmpty(manifest.identity.model_sha256) ||
            string.IsNullOrEmpty(manifest.identity.game_build_version) ||
            !string.Equals(manifest.identity.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) ||
            !string.Equals(manifest.identity.policy_signature, RlPolicySchema.Signature, StringComparison.Ordinal) ||
            manifest.identity.compatibility.policy_abi_version != RlPolicySchema.Version ||
            manifest.identity.compatibility.observation_schema_version != RlPolicySchema.ObservationSchemaVersion ||
            manifest.identity.compatibility.action_schema_version != RlPolicySchema.ActionSchemaVersion ||
            manifest.identity.compatibility.reward_schema_version != RlPolicySchema.RewardSchemaVersion ||
            manifest.identity.compatibility.scenario_schema_version != RlPolicySchema.ScenarioSchemaVersion)
        {
            manifest = null;
            return false;
        }
        return true;
    }

    private void CacheManifest(DeploymentManifest manifest)
    {
        _cachedManifest = manifest;
        _cachedDeploymentId = manifest.deployment_id;
    }

    private static string GetRootDirectory()
    {
        return Path.Combine(
            Application.persistentDataPath,
            TelemetryDirectoryName,
            $"PolicyV{RlPolicySchema.Version}");
    }

    internal static string GetPendingDirectory()
    {
        return Path.Combine(GetRootDirectory(), "Pending");
    }

    private static string GetDraftDirectory()
    {
        return Path.Combine(GetRootDirectory(), "Draft");
    }

    private static void RecoverAbandonedDrafts()
    {
        string draftDirectory = GetDraftDirectory();
        if (!Directory.Exists(draftDirectory))
        {
            return;
        }
        foreach (string path in Directory.GetFiles(draftDirectory, "*.draft.json"))
        {
            // A process exit before LevelEnded means the actual match result is unknown. Preserve the
            // samples but never invent a winner; timeout is the conservative terminal classification.
            try
            {
                TelemetryPayload payload = JsonConvert.DeserializeObject<TelemetryPayload>(File.ReadAllText(path));
                if (payload == null || payload.steps == null || payload.steps.Count == 0)
                {
                    File.Delete(path);
                    continue;
                }
                payload.result = "timeout";
                Directory.CreateDirectory(GetPendingDirectory());
                string pending = Path.Combine(GetPendingDirectory(), payload.match_id + ".json");
                WriteAtomic(pending, JsonConvert.SerializeObject(payload, Formatting.None));
                File.Delete(path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not recover abandoned live RL telemetry draft: " + exception.Message);
            }
        }
    }

    private static void WriteAtomic(string destination, string text)
    {
        string directory = Path.GetDirectoryName(destination);
        Directory.CreateDirectory(directory);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        if (File.Exists(destination))
        {
            File.Delete(temp);
            return;
        }
        File.Move(temp, destination);
    }
}
