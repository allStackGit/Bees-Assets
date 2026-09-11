using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using System;
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
/// Callers invoke this immediately before the gameplay mutation. The recorder builds the shared
/// policy observation and control action synchronously, then changes only the special-action branch.
/// </summary>
internal static class RlGameplayDemonstrationCapabilityCapture
{
    private const int CaptureManifestSchemaVersion = 1;

    private enum CaptureSource
    {
        None = 0,
        Human = 1,
        HiveMind = 2
    }

    [Serializable]
    private sealed class CaptureManifest
    {
        public int schemaVersion;
        public string behaviorName;
        public int policyAbiVersion;
        public string policySignature;
        public int observationSize;
        public int continuousActionCount;
        public int[] discreteBranchSizes;
    }

    private static readonly FieldInfo VectorObservationsField = typeof(VectorSensor).GetField(
        "m_Observations", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo RecorderLazyInitializeMethod = typeof(DemonstrationRecorder).GetMethod(
        "LazyInitialize", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriterRecordMethod = typeof(DemonstrationWriter).GetMethod(
        "Record", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly bool CaptureRequested = RlGameplayDemonstrationAgent.IsCaptureRequested(
        Environment.GetCommandLineArgs());

    internal static bool RuntimeContractsAvailableForTests =>
        VectorObservationsField != null && RecorderLazyInitializeMethod != null && WriterRecordMethod != null;

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
        if (source == CaptureSource.None ||
            !TryCaptureCurrentState(
                ship,
                specialAction,
                out float[] observation,
                out float[] continuousActions,
                out int[] discreteActions))
        {
            return;
        }

        string root = GetPolicyDirectory();
        if (!HasCompatibleCaptureManifest(root))
        {
            return;
        }

        WriteEventEpisode(ship, source, observation, continuousActions, discreteActions);
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

    private static bool HasCompatibleCaptureManifest(string root)
    {
        string path = Path.Combine(root, RlGameplayDemonstrationAgent.CaptureManifestFileName);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            CaptureManifest manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(path));
            if (manifest == null ||
                manifest.schemaVersion != CaptureManifestSchemaVersion ||
                !string.Equals(manifest.behaviorName, RlOneVsOneAgent.BehaviorName, StringComparison.Ordinal) ||
                manifest.policyAbiVersion != RlPolicySchema.Version ||
                !string.Equals(manifest.policySignature, RlPolicySchema.Signature, StringComparison.Ordinal) ||
                manifest.observationSize != RlOneVsOneAgent.ObservationSize ||
                manifest.continuousActionCount != RlOneVsOneAgent.ContinuousActionCount)
            {
                return false;
            }

            int[] expectedBranches = RlOneVsOneAgent.CreateDiscreteBranchSizes();
            if (manifest.discreteBranchSizes == null || manifest.discreteBranchSizes.Length != expectedBranches.Length)
            {
                return false;
            }
            for (int i = 0; i < expectedBranches.Length; i++)
            {
                if (manifest.discreteBranchSizes[i] != expectedBranches[i])
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCaptureCurrentState(
        Ship ship,
        int specialAction,
        out float[] observationSnapshot,
        out float[] continuousSnapshot,
        out int[] discreteSnapshot)
    {
        observationSnapshot = null;
        continuousSnapshot = null;
        discreteSnapshot = null;

        VectorSensor sensor = new VectorSensor(RlOneVsOneAgent.ObservationSize);
        RlCombatPerception perception = new RlCombatPerception();
        perception.Collect(ship, ship.Side, sensor, 0);
        if (!(VectorObservationsField.GetValue(sensor) is List<float> observations))
        {
            return false;
        }

        ActionBuffers actions = EncodeCurrentAction(ship, specialAction);
        return TryCreateSample(
            observations,
            actions,
            specialAction,
            out observationSnapshot,
            out continuousSnapshot,
            out discreteSnapshot);
    }

    private static ActionBuffers EncodeCurrentAction(Ship ship, int specialAction)
    {
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
        return new ActionBuffers(continuous, discrete);
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
                : $"hivemind-cap-s{ship.Side}";
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
