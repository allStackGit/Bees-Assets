using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Opt-in diagnostic that writes one raw RL observation vector and then disables itself.
/// No object is created and no capture work occurs unless the command-line flag is present.
/// </summary>
internal sealed class RlObservationSnapshotCapture : MonoBehaviour
{
    internal const string CaptureFlag = "--bees-rl-observation-snapshot";
    internal const string OutputFlag = "--bees-rl-observation-snapshot-output";

    private static readonly MethodInfo GetObservationsMethod = typeof(VectorSensor).GetMethod(
        "GetObservations",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private string _outputPath;

    [Serializable]
    private sealed class SnapshotPayload
    {
        public string utc_timestamp;
        public int process_id;
        public int unity_frame;
        public string agent_name;
        public int agent_instance_id;
        public string behavior_name;
        public int policy_abi_version;
        public string policy_abi_signature;
        public int observation_count;
        public bool raw_pre_normalization;
        public float[] observations;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InitializeFromCommandLine()
    {
        if (!TryParseCommandLine(Environment.GetCommandLineArgs(), out string outputPath, out string error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError("[RL] Observation snapshot capture disabled: " + error);
            }
            return;
        }

        GameObject captureObject = new GameObject(nameof(RlObservationSnapshotCapture));
        DontDestroyOnLoad(captureObject);
        RlObservationSnapshotCapture capture = captureObject.AddComponent<RlObservationSnapshotCapture>();
        capture._outputPath = outputPath;
    }

    private IEnumerator Start()
    {
        if (GetObservationsMethod == null)
        {
            Debug.LogError("[RL] Observation snapshot capture could not access VectorSensor.GetObservations().");
            Destroy(gameObject);
            yield break;
        }

        while (true)
        {
            RlOneVsOneAgent[] agents = FindObjectsByType<RlOneVsOneAgent>(FindObjectsSortMode.None);
            for (int i = 0; i < agents.Length; i++)
            {
                if (TryCapture(agents[i]))
                {
                    Destroy(gameObject);
                    yield break;
                }
            }

            yield return null;
        }
    }

    private bool TryCapture(RlOneVsOneAgent agent)
    {
        if (agent == null || !agent.isActiveAndEnabled)
        {
            return false;
        }

        VectorSensor sensor = new VectorSensor(RlPolicySchema.ExpectedObservationSize, "RlObservationSnapshot");
        agent.CollectObservations(sensor);

        object raw = GetObservationsMethod.Invoke(sensor, null);
        IEnumerable<float> values = raw as IEnumerable<float>;
        if (values == null)
        {
            return false;
        }

        List<float> observations = new List<float>(RlPolicySchema.ExpectedObservationSize);
        bool hasNonZeroObservation = false;
        foreach (float value in values)
        {
            observations.Add(value);
            if (value != 0f)
            {
                hasNonZeroObservation = true;
            }
        }

        if (observations.Count != RlPolicySchema.ExpectedObservationSize || !hasNonZeroObservation)
        {
            return false;
        }

        SnapshotPayload payload = new SnapshotPayload
        {
            utc_timestamp = DateTime.UtcNow.ToString("O"),
            process_id = System.Diagnostics.Process.GetCurrentProcess().Id,
            unity_frame = Time.frameCount,
            agent_name = agent.name,
            agent_instance_id = agent.GetInstanceID(),
            behavior_name = RlPolicySchema.ExpectedBehaviorName,
            policy_abi_version = RlPolicySchema.Version,
            policy_abi_signature = RlPolicySchema.Signature,
            observation_count = observations.Count,
            raw_pre_normalization = true,
            observations = observations.ToArray()
        };

        try
        {
            string fullPath = Path.GetFullPath(_outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, JsonUtility.ToJson(payload, true));
            Debug.Log($"[RL] Wrote one-shot observation snapshot ({observations.Count} values) to {fullPath}");
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[RL] Observation snapshot capture failed for '{_outputPath}': {exception.Message}");
            Destroy(gameObject);
            return false;
        }
    }

    private static bool TryParseCommandLine(string[] args, out string outputPath, out string error)
    {
        bool requested = false;
        outputPath = null;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, CaptureFlag, StringComparison.Ordinal))
            {
                requested = true;
                continue;
            }

            string outputPrefix = OutputFlag + "=";
            if (arg.StartsWith(outputPrefix, StringComparison.Ordinal))
            {
                requested = true;
                outputPath = arg.Substring(outputPrefix.Length);
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    error = OutputFlag + " requires a non-empty path.";
                    return false;
                }
                continue;
            }

            if (!string.Equals(arg, OutputFlag, StringComparison.Ordinal))
            {
                continue;
            }

            requested = true;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = OutputFlag + " requires a path argument.";
                return false;
            }

            outputPath = args[++i];
        }

        if (!requested)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            outputPath = Path.Combine(Environment.CurrentDirectory, $"rl-observation-snapshot-{processId}.json");
        }

        return true;
    }
}
