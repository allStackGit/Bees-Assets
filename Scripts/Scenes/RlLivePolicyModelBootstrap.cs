using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// Binds the build-time installed continual-learning champion to player-facing live RL agents.
/// The authoritative deployment package is verified before build staging; this runtime boundary
/// independently checks the bundled manifest against the frozen policy ABI before assigning the
/// imported ModelAsset. Missing/incompatible assets fail back to the existing Hive Mind controller
/// rather than leaving AI ships on RlLivePolicyAgent's intentionally inert heuristic.
/// </summary>
[DefaultExecutionOrder(-10000)]
internal sealed class RlLivePolicyModelBootstrap : MonoBehaviour
{
    internal const string ModelResourcePath = "RlPolicy/BeesRL1v1";
    internal const string ManifestResourcePath = "RlPolicy/BeesRL1v1Deployment";
    internal const int DeploymentManifestSchemaVersion = 1;

    [Serializable]
    private sealed class DeploymentCompatibility
    {
        public string behavior_name;
        public int policy_abi_version;
        public int observation_schema_version;
        public int action_schema_version;
        public int reward_schema_version;
        public int scenario_schema_version;
    }

    [Serializable]
    private sealed class DeploymentIdentity
    {
        public int schema_version;
        public string model_id;
        public string model_sha256;
        public long model_size_bytes;
        public string behavior_name;
        public string policy_signature;
        public DeploymentCompatibility compatibility;
        public string game_build_version;
        public string training_run_id;
        public long training_step;
    }

    [Serializable]
    private sealed class DeploymentManifest
    {
        public int schema_version;
        public string deployment_id;
        public string identity_sha256;
        public DeploymentIdentity identity;
        public string model_file;
    }

    private readonly Dictionary<Level, int> _knownLevelChildCounts = new Dictionary<Level, int>();
    private Stage _stage;
    private ModelAsset _model;
    private string _deploymentId;
    private string _modelId;
    private bool _bindingActive;
    private bool _fallbackPending;
    private bool _fallbackStarted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForPlayerFacingStage()
    {
        if (RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (!IsLiveRlRequested(stage))
        {
            return;
        }

        RlLivePolicyModelBootstrap bootstrap = stage.GetComponent<RlLivePolicyModelBootstrap>();
        if (bootstrap == null)
        {
            bootstrap = stage.gameObject.AddComponent<RlLivePolicyModelBootstrap>();
        }
        bootstrap._stage = stage;

        if (!TryLoadBundle(out ModelAsset model, out string deploymentId, out string modelId, out string error))
        {
            bootstrap.FailToHiveMind(error);
            return;
        }

        bootstrap._model = model;
        bootstrap._deploymentId = deploymentId;
        bootstrap._modelId = modelId;
        bootstrap._bindingActive = true;

        RlLivePolicyModelUpdater updater = stage.GetComponent<RlLivePolicyModelUpdater>();
        if (updater == null)
        {
            updater = stage.gameObject.AddComponent<RlLivePolicyModelUpdater>();
        }
        updater.Initialize(bootstrap, deploymentId);

        Debug.Log(
            $"Live RL champion bundle ready: deployment={deploymentId} model={modelId} " +
            $"ABI=v{RlPolicySchema.Version} resource={ModelResourcePath}.");
    }

    private static bool IsLiveRlRequested(Stage stage)
    {
        return stage != null && stage.ActivateHiveMind && stage.ActivateBrains;
    }

    private static bool TryLoadBundle(
        out ModelAsset model,
        out string deploymentId,
        out string modelId,
        out string error)
    {
        model = null;
        deploymentId = null;
        modelId = null;
        error = null;

        try
        {
            RlPolicySchema.ValidateOrThrow();
        }
        catch (Exception exception)
        {
            error = "compiled RL policy schema is invalid: " + exception.Message;
            return false;
        }

        TextAsset manifestAsset = Resources.Load<TextAsset>(ManifestResourcePath);
        if (manifestAsset == null)
        {
            error = $"deployment manifest resource {ManifestResourcePath} is missing";
            return false;
        }
        if (!TryValidateManifestJson(manifestAsset.text, out DeploymentManifest manifest, out error))
        {
            return false;
        }

        model = Resources.Load<ModelAsset>(ModelResourcePath);
        if (model == null)
        {
            error = $"imported champion ModelAsset resource {ModelResourcePath} is missing";
            return false;
        }

        deploymentId = manifest.deployment_id;
        modelId = manifest.identity.model_id;
        return true;
    }

    private static bool TryValidateManifestJson(
        string json,
        out DeploymentManifest manifest,
        out string error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "deployment manifest is empty";
            return false;
        }

        try
        {
            manifest = JsonUtility.FromJson<DeploymentManifest>(json);
        }
        catch (Exception exception)
        {
            error = "deployment manifest JSON could not be parsed: " + exception.Message;
            return false;
        }
        if (manifest == null || manifest.schema_version != DeploymentManifestSchemaVersion || manifest.identity == null)
        {
            error = "deployment manifest schema/identity is incompatible";
            return false;
        }
        if (!IsContentId(manifest.deployment_id, "deploy-", 24) || !IsLowerHex(manifest.identity_sha256, 64))
        {
            error = "deployment manifest content identity is malformed";
            return false;
        }
        if (!manifest.deployment_id.Equals(
                "deploy-" + manifest.identity_sha256.Substring(0, 24),
                StringComparison.Ordinal))
        {
            error = "deployment id does not match its declared identity hash";
            return false;
        }
        if (!string.Equals(manifest.model_file, "model.onnx", StringComparison.Ordinal))
        {
            error = "deployment manifest model_file is incompatible";
            return false;
        }

        DeploymentIdentity identity = manifest.identity;
        if (identity.schema_version != DeploymentManifestSchemaVersion ||
            !IsContentId(identity.model_id, $"bees-rl-v{RlPolicySchema.Version}-", 24) ||
            !IsLowerHex(identity.model_sha256, 64) ||
            identity.model_size_bytes <= 0)
        {
            error = "deployment model identity is malformed";
            return false;
        }
        if (!string.Equals(identity.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) ||
            !string.Equals(identity.policy_signature, RlPolicySchema.Signature, StringComparison.Ordinal))
        {
            error = "deployment behavior or frozen policy signature does not match this build";
            return false;
        }
        if (identity.compatibility == null ||
            !string.Equals(identity.compatibility.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) ||
            identity.compatibility.policy_abi_version != RlPolicySchema.Version)
        {
            error = "deployment compatibility does not match this build's policy ABI";
            return false;
        }
        if (string.IsNullOrWhiteSpace(identity.game_build_version) ||
            string.IsNullOrWhiteSpace(identity.training_run_id) ||
            identity.training_step < 0)
        {
            error = "deployment model lineage is incomplete";
            return false;
        }
        return true;
    }

    internal static bool TryValidateManifestJsonForTests(string json, out string error)
    {
        return TryValidateManifestJson(json, out _, out error);
    }

    internal bool TryApplyHotBundle(
        ModelAsset model,
        string manifestJson,
        string expectedDeploymentId,
        string expectedModelId,
        out string error)
    {
        error = null;
        if (!_bindingActive || _stage == null || _fallbackPending || _fallbackStarted)
        {
            error = "live RL policy bootstrap is not in an active swappable state";
            return false;
        }
        if (model == null)
        {
            error = "downloaded champion ModelAsset is missing";
            return false;
        }
        if (!TryValidateManifestJson(manifestJson, out DeploymentManifest manifest, out error))
        {
            return false;
        }
        if (!string.Equals(manifest.deployment_id, expectedDeploymentId, StringComparison.Ordinal) ||
            !string.Equals(manifest.identity.model_id, expectedModelId, StringComparison.Ordinal))
        {
            error = "downloaded bundle manifest does not match the authenticated server deployment/model identity";
            return false;
        }
        if (string.Equals(_deploymentId, manifest.deployment_id, StringComparison.Ordinal))
        {
            return true;
        }

        ModelAsset previousModel = _model;
        RlLivePolicyAgent[] agents = _stage.GetComponentsInChildren<RlLivePolicyAgent>(true);
        List<RlLivePolicyAgent> changedAgents = new List<RlLivePolicyAgent>(agents.Length);
        for (int i = 0; i < agents.Length; i++)
        {
            RlLivePolicyAgent agent = agents[i];
            if (agent == null)
            {
                continue;
            }
            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior == null)
            {
                error = "live RL agent is missing BehaviorParameters";
                return false;
            }
            if (behavior.Model != null && behavior.Model != previousModel)
            {
                error = "live RL agent already has a different inference model";
                return false;
            }
        }

        try
        {
            for (int i = 0; i < agents.Length; i++)
            {
                RlLivePolicyAgent agent = agents[i];
                if (agent == null)
                {
                    continue;
                }
                BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
                if (behavior.Model != previousModel)
                {
                    continue;
                }
                agent.SetModel(RlOneVsOneAgent.BehaviorName, model);
                behavior.BehaviorType = BehaviorType.InferenceOnly;
                changedAgents.Add(agent);
            }
        }
        catch (Exception exception)
        {
            bool rollbackFailed = false;
            for (int i = changedAgents.Count - 1; i >= 0; i--)
            {
                try
                {
                    RlLivePolicyAgent agent = changedAgents[i];
                    agent.SetModel(RlOneVsOneAgent.BehaviorName, previousModel);
                    BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
                    if (behavior != null)
                    {
                        behavior.BehaviorType = BehaviorType.InferenceOnly;
                    }
                }
                catch
                {
                    rollbackFailed = true;
                }
            }

            error = "could not hot-swap live RL champion: " + exception.Message;
            if (rollbackFailed)
            {
                FailToHiveMind(error + "; rollback to the prior champion also failed");
            }
            return false;
        }

        _model = model;
        _deploymentId = manifest.deployment_id;
        _modelId = manifest.identity.model_id;
        _knownLevelChildCounts.Clear();
        return true;
    }

    private void FixedUpdate()
    {
        if (_fallbackPending)
        {
            TryStartHiveMindFallback();
            return;
        }
        if (!_bindingActive || _stage == null || !IsLiveRlRequested(_stage) ||
            !_stage.IsFinalized || ConfigData.Configuration == null)
        {
            return;
        }

        IReadOnlyList<Level> levels = _stage.Levels;
        for (int i = 0; i < levels.Count; i++)
        {
            Level level = levels[i];
            if (level == null)
            {
                continue;
            }
            int childCount = level.transform.childCount;
            if (_knownLevelChildCounts.TryGetValue(level, out int knownChildCount) && knownChildCount == childCount)
            {
                continue;
            }
            _knownLevelChildCounts[level] = childCount;
            if (!BindLevelAgents(level))
            {
                return;
            }
        }
    }

    private bool BindLevelAgents(Level level)
    {
        RlLivePolicyAgent[] agents = level.GetComponentsInChildren<RlLivePolicyAgent>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            RlLivePolicyAgent agent = agents[i];
            if (agent == null)
            {
                continue;
            }
            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior == null)
            {
                FailToHiveMind("live RL agent is missing BehaviorParameters");
                return false;
            }
            if (behavior.Model == _model && behavior.BehaviorType == BehaviorType.InferenceOnly)
            {
                continue;
            }
            if (behavior.Model != null && behavior.Model != _model)
            {
                FailToHiveMind("live RL agent already has a different inference model");
                return false;
            }

            try
            {
                if (behavior.Model != _model)
                {
                    agent.SetModel(RlOneVsOneAgent.BehaviorName, _model);
                }
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            catch (Exception exception)
            {
                FailToHiveMind(
                    $"could not bind deployment {_deploymentId} model {_modelId}: " + exception.Message);
                return false;
            }
        }
        return true;
    }

    private void FailToHiveMind(string reason)
    {
        if (_stage == null || _fallbackPending || _fallbackStarted)
        {
            return;
        }
        _bindingActive = false;
        _stage.ActivateBrains = false;
        _fallbackPending = true;
        Debug.LogError(
            "Live RL champion unavailable/incompatible; reverting AI ownership to Hive Mind: " + reason);
        TryStartHiveMindFallback();
    }

    private void TryStartHiveMindFallback()
    {
        if (!_fallbackPending || _fallbackStarted || _stage == null ||
            !_stage.IsFinalized || ConfigData.Configuration == null)
        {
            return;
        }

        RlLivePolicyAgent[] agents = _stage.GetComponentsInChildren<RlLivePolicyAgent>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            if (agents[i] != null)
            {
                agents[i].enabled = false;
            }
        }

        IReadOnlyList<Level> levels = _stage.Levels;
        for (int i = 0; i < levels.Count; i++)
        {
            Level level = levels[i];
            if (level != null)
            {
                level.SetupHivemind();
            }
        }
        _fallbackStarted = true;
        _fallbackPending = false;
        enabled = false;
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
}
