using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Opt-in diagnostic that samples live RL observations while the command-line flag is present.
/// Every twentieth valid sample is appended to one JSON array. No object is created and no
/// capture work occurs unless the command-line flag is present.
/// </summary>
internal sealed class RlObservationSnapshotCapture : MonoBehaviour
{
    internal const string CaptureFlag = "--bees-rl-observation-snapshot";
    internal const string OutputFlag = "--bees-rl-observation-snapshot-output";
    internal const int CaptureInterval = 20;

    private static readonly MethodInfo GetObservationsMethod = typeof(VectorSensor).GetMethod(
        "GetObservations",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private string _outputPath;
    private long _validObservationCount;
    private long _snapshotCount;
    private bool _outputInitialized;

    [Serializable]
    private sealed class SnapshotPayload
    {
        public string utc_timestamp;
        public int process_id;
        public int unity_frame;
        public long observation_sequence;
        public long snapshot_sequence;
        public int capture_interval;
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

        Debug.Log($"[RL] Observation snapshot capture enabled; writing every {CaptureInterval}th valid observation to {_outputPath}");

        while (true)
        {
            RlOneVsOneAgent[] agents = FindObjectsByType<RlOneVsOneAgent>(FindObjectsSortMode.None);
            for (int i = 0; i < agents.Length; i++)
            {
                TrySample(agents[i]);
            }

            yield return null;
        }
    }

    private void TrySample(RlOneVsOneAgent agent)
    {
        if (agent == null || !agent.isActiveAndEnabled)
        {
            return;
        }

        VectorSensor sensor = new VectorSensor(RlPolicySchema.ExpectedObservationSize, "RlObservationSnapshot");
        agent.CollectObservations(sensor);

        object raw = GetObservationsMethod.Invoke(sensor, null);
        IEnumerable<float> values = raw as IEnumerable<float>;
        if (values == null)
        {
            return;
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
            return;
        }

        _validObservationCount++;
        if (_validObservationCount % CaptureInterval != 0)
        {
            return;
        }

        _snapshotCount++;
        SnapshotPayload payload = new SnapshotPayload
        {
            utc_timestamp = DateTime.UtcNow.ToString("O"),
            process_id = System.Diagnostics.Process.GetCurrentProcess().Id,
            unity_frame = Time.frameCount,
            observation_sequence = _validObservationCount,
            snapshot_sequence = _snapshotCount,
            capture_interval = CaptureInterval,
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
            AppendSnapshot(payload);
            Debug.Log($"[RL] Appended observation snapshot {_snapshotCount} from observation {_validObservationCount} ({observations.Count} values) to {Path.GetFullPath(_outputPath)}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[RL] Observation snapshot capture failed for '{_outputPath}': {exception.Message}");
            Destroy(gameObject);
        }
    }

    private void AppendSnapshot(SnapshotPayload payload)
    {
        string fullPath = Path.GetFullPath(_outputPath);
        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonUtility.ToJson(payload, true);
        if (!_outputInitialized)
        {
            File.WriteAllText(fullPath, "[\n" + json + "\n]\n");
            _outputInitialized = true;
            return;
        }

        using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            long closingBracketPosition = FindClosingArrayBracket(stream);
            if (closingBracketPosition < 0)
            {
                throw new InvalidDataException("Observation snapshot file is not a valid JSON array.");
            }

            stream.SetLength(closingBracketPosition);
            stream.Position = closingBracketPosition;
            byte[] appended = Encoding.UTF8.GetBytes(",\n" + json + "\n]\n");
            stream.Write(appended, 0, appended.Length);
            stream.Flush();
        }
    }

    private static long FindClosingArrayBracket(FileStream stream)
    {
        for (long position = stream.Length - 1; position >= 0; position--)
        {
            stream.Position = position;
            int value = stream.ReadByte();
            if (value < 0)
            {
                continue;
            }

            char character = (char)value;
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character == ']' ? position : -1;
        }

        return -1;
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
