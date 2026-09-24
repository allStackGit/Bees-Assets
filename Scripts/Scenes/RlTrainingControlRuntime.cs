using Newtonsoft.Json.Linq;
using System;
using System.IO;
using Unity.MLAgents.Policies;

/// <summary>
/// Reads the local state written by bees_training_worker_agent.py. This path is present only for
/// centrally managed processes. Full-game agents fail closed to deployed-policy inference whenever
/// the control lease is offline or BeesServer says training is not active.
/// </summary>
internal static class RlTrainingControlRuntime
{
    internal const string StateFileEnvironmentVariable = "BEES_TRAINING_CONTROL_STATE_FILE";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private static string _path;
    private static DateTime _nextRefreshUtc = DateTime.MinValue;
    private static bool _forceInference = true;

    internal static bool ShouldForceInference(bool online, string desiredMode)
    {
        return !online ||
               !string.Equals(desiredMode, "training", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseState(string json, out bool forceInference)
    {
        forceInference = true;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JObject value;
        try
        {
            value = JObject.Parse(json);
        }
        catch
        {
            return false;
        }

        JToken onlineToken = value["online"];
        JToken modeToken = value["desired_mode"];
        if (onlineToken == null || onlineToken.Type != JTokenType.Boolean ||
            modeToken == null || modeToken.Type != JTokenType.String)
        {
            return false;
        }

        bool online = onlineToken.Value<bool>();
        string desiredMode = modeToken.Value<string>();
        if (!string.Equals(desiredMode, "training", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(desiredMode, "inference", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(desiredMode, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        forceInference = ShouldForceInference(online, desiredMode);
        return true;
    }

    internal static bool TryGetForceInference(out bool forceInference)
    {
        string path = Environment.GetEnvironmentVariable(StateFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            forceInference = false;
            return false;
        }

        Refresh(path);
        forceInference = _forceInference;
        return true;
    }

    internal static void Apply(BehaviorParameters behavior)
    {
        if (behavior == null || !TryGetForceInference(out bool forceInference))
        {
            return;
        }

        BehaviorType desired = forceInference
            ? BehaviorType.InferenceOnly
            : BehaviorType.Default;
        if (behavior.BehaviorType != desired)
        {
            behavior.BehaviorType = desired;
        }
    }

    private static void Refresh(string path)
    {
        DateTime now = DateTime.UtcNow;
        if (string.Equals(_path, path, StringComparison.Ordinal) && now < _nextRefreshUtc)
        {
            return;
        }

        _path = path;
        _nextRefreshUtc = now + RefreshInterval;
        try
        {
            if (!File.Exists(path))
            {
                _forceInference = true;
                return;
            }

            if (!TryParseState(File.ReadAllText(path), out bool forceInference))
            {
                _forceInference = true;
                return;
            }
            _forceInference = forceInference;
        }
        catch
        {
            // A managed full game must never require central control to keep playing.
            _forceInference = true;
        }
    }

    internal static void ResetForTests()
    {
        _path = null;
        _nextRefreshUtc = DateTime.MinValue;
        _forceInference = true;
    }
}
