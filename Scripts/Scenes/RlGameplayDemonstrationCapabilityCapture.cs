using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Demonstrations;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Writes successful one-shot gameplay capabilities as isolated ML-Agents demonstration episodes.
/// The sample uses the passive controller's most recent policy observation and action, so an event
/// is paired with the decision state that preceded it rather than with state changed by the event.
/// </summary>
internal static class RlGameplayDemonstrationCapabilityCapture
{
    private enum CaptureSource
    {
        None = 0,
        Human = 1,
        HiveMind = 2
    }

    private static readonly FieldInfo PassiveInstancesField = typeof(RlGameplayDemonstrationAgent).GetField(
        "Instances", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo PassiveShipField = typeof(RlGameplayDemonstrationAgent).GetField(
        "_ship", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo RecorderLazyInitializeMethod = typeof(DemonstrationRecorder).GetMethod(
        "LazyInitialize", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriterRecordMethod = typeof(DemonstrationWriter).GetMethod(
        "Record", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly bool CaptureRequested = RlGameplayDemonstrationAgent.IsCaptureRequested(
        Environment.GetCommandLineArgs());

    internal static bool RuntimeContractsAvailableForTests =>
        PassiveInstancesField != null && PassiveShipField != null &&
        RecorderLazyInitializeMethod != null && WriterRecordMethod != null;

    internal static void Record(Ship ship, int specialAction)
    {
        if (!CaptureRequested || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            ship == null || ship.IsDead || !RuntimeContractsAvailableForTests ||
            specialAction <= RlOneVsOneAgent.NoSpecialAction ||
            specialAction >= RlOneVsOneAgent.SpecialActionBranchSize)
        {
            return;
        }

        CaptureSource source = DetermineSource(ship);
        if (source == CaptureSource.None || !TryFindPassiveAgent(ship, out RlGameplayDemonstrationAgent passiveAgent))
        {
            return;
        }

        if (!TryCreateSample(
                passiveAgent.GetObservations(),
                passiveAgent.GetStoredActionBuffers(),
                specialAction,
                out float[] observation,
                out float[] continuousActions,
                out int[] discreteActions))
        {
            return;
        }

        string root = GetPolicyDirectory();
        if (!File.Exists(Path.Combine(root, RlGameplayDemonstrationAgent.CaptureManifestFileName)))
        {
            return;
        }

        WriteEventEpisode(
            ship,
            source,
            observation,
            continuousActions,
            discreteActions);
    }

    private static bool TryFindPassiveAgent(Ship ship, out RlGameplayDemonstrationAgent match)
    {
        match = null;
        if (!(PassiveInstancesField.GetValue(null) is IEnumerable instances))
        {
            return false;
        }

        foreach (object item in instances)
        {
            if (!(item is RlGameplayDemonstrationAgent candidate))
            {
                continue;
            }

            if (ReferenceEquals(PassiveShipField.GetValue(candidate), ship))
            {
                match = candidate;
                return true;
            }
        }
        return false;
    }

    private static CaptureSource DetermineSource(Ship ship)
    {
        if (ship == null || ship.Squad == null || ship.Level == null ||
            ship.Level.Stage == null || ConfigData.Configuration == null)
        {
            return CaptureSource.None;
        }

        Stage stage = ship.Level.Stage;
        bool liveRlControlled = stage.ActivateHiveMind && stage.ActivateBrains &&
            RlLivePolicyAgent.ShouldControlSide(
                ship.Level.HasPlayer,
                ship.Side,
                ConfigData.Configuration.AISide);
        if (liveRlControlled)
        {
            return CaptureSource.None;
        }
        if (ship.Squad.IsUserControlled)
        {
            return CaptureSource.Human;
        }
        if (ship.Squad.IsHiveMindControlled && stage.ActivateHiveMind && !stage.ActivateBrains)
        {
            return CaptureSource.HiveMind;
        }
        return CaptureSource.None;
    }

    private static bool TryCreateSample(
        IReadOnlyList<float> observations,
        ActionBuffers storedActions,
        int specialAction,
        out float[] observationSnapshot,
        out float[] continuousSnapshot,
        out int[] discreteSnapshot)
    {
        observationSnapshot = null;
        continuousSnapshot = null;
        discreteSnapshot = null;

        if (observations == null || observations.Count != RlOneVsOneAgent.ObservationSize ||
            storedActions.ContinuousActions.Length != RlOneVsOneAgent.ContinuousActionCount ||
            storedActions.DiscreteActions.Length != RlOneVsOneAgent.DiscreteBranchCount ||
            specialAction <= RlOneVsOneAgent.NoSpecialAction ||
            specialAction >= RlOneVsOneAgent.SpecialActionBranchSize)
        {
            return false;
        }

        observationSnapshot = new float[RlOneVsOneAgent.ObservationSize];
        continuousSnapshot = new float[RlOneVsOneAgent.ContinuousActionCount];
        discreteSnapshot = new int[RlOneVsOneAgent.DiscreteBranchCount];

        for (int i = 0; i < observationSnapshot.Length; i++)
        {
            observationSnapshot[i] = observations[i];
        }
        for (int i = 0; i < continuousSnapshot.Length; i++)
        {
            continuousSnapshot[i] = storedActions.ContinuousActions[i];
        }
        for (int i = 0; i < discreteSnapshot.Length; i++)
        {
            discreteSnapshot[i] = storedActions.DiscreteActions[i];
        }
        discreteSnapshot[RlOneVsOneAgent.SpecialActionBranch] = specialAction;
        return true;
    }

    private static void WriteEventEpisode(
        Ship ship,
        CaptureSource source,
        float[] observation,
        float[] continuousActions,
        int[] discreteActions)
    {
        GameObject obj = new GameObject($"RL Capability Demonstration - {source} - {ship.Side}");
        try
        {
            obj.transform.SetParent(ship.Level.transform, false);

            BehaviorParameters behavior = obj.AddComponent<BehaviorParameters>();
            behavior.BehaviorName = RlOneVsOneAgent.BehaviorName;
            behavior.TeamId = 0;
            behavior.BehaviorType = BehaviorType.HeuristicOnly;
            behavior.BrainParameters.VectorObservationSize = RlOneVsOneAgent.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = new ActionSpec(
                RlOneVsOneAgent.ContinuousActionCount,
                RlOneVsOneAgent.CreateDiscreteBranchSizes());

            Agent agent = obj.AddComponent<Agent>();
            agent.LazyInitialize();

            DemonstrationRecorder recorder = obj.AddComponent<DemonstrationRecorder>();
            recorder.DemonstrationName = source == CaptureSource.Human
                ? $"human-cap-s{ship.Side}"
                : $"hive-cap-s{ship.Side}";
            recorder.DemonstrationDirectory = Path.Combine(GetPolicyDirectory(), GetSourceDirectoryName(source));
            recorder.NumStepsToRecord = 0;
            recorder.Record = true;

            DemonstrationWriter writer = RecorderLazyInitializeMethod.Invoke(
                recorder,
                new object[] { null }) as DemonstrationWriter;
            if (writer == null)
            {
                return;
            }

            VectorSensor sensor = new VectorSensor(RlOneVsOneAgent.ObservationSize);
            sensor.AddObservation(observation);
            List<ISensor> sensors = new List<ISensor>(1) { sensor };

            AgentInfo eventInfo = new AgentInfo
            {
                storedActions = new ActionBuffers(continuousActions, discreteActions),
                done = false,
                maxStepReached = false,
                episodeId = 0,
                groupId = 0,
                reward = 0f,
                groupReward = 0f
            };
            WriterRecordMethod.Invoke(writer, new object[] { eventInfo, sensors });

            AgentInfo terminalInfo = eventInfo;
            terminalInfo.done = true;
            WriterRecordMethod.Invoke(writer, new object[] { terminalInfo, sensors });
            recorder.Close();
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"RL capability demonstration capture skipped: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            UnityEngine.Object.Destroy(obj);
        }
    }

    private static string GetPolicyDirectory()
    {
        return Path.Combine(
            Application.persistentDataPath,
            RlGameplayDemonstrationAgent.DemonstrationDirectoryName,
            $"PolicyV{RlPolicySchema.Version}");
    }

    private static string GetSourceDirectoryName(CaptureSource source)
    {
        return source == CaptureSource.Human ? "Human" : "HiveMind";
    }
}
