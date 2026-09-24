using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Applies side-specific controller ownership to ordinary production Stages and reapplies it whenever
/// a pooled Level starts a new generation. It deliberately does not set Stage.IsTraining: production
/// gameplay keeps its normal UI/objective lifecycle while contributing learning data out-of-band.
/// </summary>
[DefaultExecutionOrder(-12000)]
internal sealed class RlProductionControllerCoordinator : MonoBehaviour
{
    private readonly Dictionary<Level, float> _knownStartTimes = new Dictionary<Level, float>();
    private Stage _stage;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = UnityEngine.Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            return;
        }
        RlProductionControllerCoordinator coordinator =
            stage.GetComponent<RlProductionControllerCoordinator>();
        if (coordinator == null)
        {
            coordinator = stage.gameObject.AddComponent<RlProductionControllerCoordinator>();
        }
        coordinator._stage = stage;
        stage.StartCoroutine(coordinator.ConfigureWhenReady());
    }

    private IEnumerator ConfigureWhenReady()
    {
        while (_stage != null && (!_stage.IsFinalized || ConfigData.Configuration == null))
        {
            yield return null;
        }
        if (_stage == null)
        {
            yield break;
        }

        ApplyStageCompatibilityFlags();
        ConfigureAllLevels(force: true);
    }

    private void FixedUpdate()
    {
        if (_stage == null || !_stage.IsFinalized || ConfigData.Configuration == null)
        {
            return;
        }
        ApplyStageCompatibilityFlags();
        ConfigureAllLevels(force: false);
    }

    private void ApplyStageCompatibilityFlags()
    {
        bool hasNn = RlProductionControllerRouter.AnyNeuralNetwork(_stage);
        bool hasHive = RlProductionControllerRouter.AnyHiveMind(_stage);
        if (hasNn || hasHive)
        {
            // Several legacy combat paths use ActivateHiveMind as the broad "AI enabled" gate.
            _stage.ActivateHiveMind = true;
        }
        if (hasNn)
        {
            // Keep old scene/runtime consumers aware that neural control exists. Mixed ownership is
            // corrected per side by ConfigureProductionControllerOwnership below.
            _stage.ActivateRlPolicy = true;
        }
    }

    private void ConfigureAllLevels(bool force)
    {
        IReadOnlyList<Level> levels = _stage.Levels;
        for (int i = 0; i < levels.Count; i++)
        {
            Level level = levels[i];
            if (level == null || level.State == null)
            {
                continue;
            }

            float startTime = level.StartTime;
            if (!force && _knownStartTimes.TryGetValue(level, out float known) &&
                Mathf.Approximately(known, startTime))
            {
                continue;
            }
            _knownStartTimes[level] = startTime;
            level.ConfigureProductionControllerOwnership();
            RlProductionEpisodeBoundary.NotifyGenerationStarted(level, startTime);
        }
    }
}

/// <summary>
/// Small episode-boundary signal shared by model pinning and rollout capture. A reused Level's
/// StartTime is already the durable generation boundary elsewhere in the RL telemetry path.
/// </summary>
internal static class RlProductionEpisodeBoundary
{
    internal static event Action<Level, float> GenerationStarted;

    internal static void NotifyGenerationStarted(Level level, float startTime)
    {
        GenerationStarted?.Invoke(level, startTime);
    }
}
