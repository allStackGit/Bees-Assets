using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;

/// <summary>
/// Resolves who owns each combat side in ordinary production gameplay. The legacy Stage booleans
/// remain the serialized default, while per-side command-line overrides make mixed Hive Mind / NN
/// and diagnostic pairings possible without changing scene assets.
/// </summary>
internal static class RlProductionControllerRouter
{
    internal enum ControllerKind
    {
        None = 0,
        Player = 1,
        HiveMind = 2,
        NeuralNetwork = 3
    }

    internal const string BeeControllerFlag = "--rl-bee-controller";
    internal const string HumanControllerFlag = "--rl-human-controller";

    private static bool _overridesInitialized;
    private static ControllerKind? _beeOverride;
    private static ControllerKind? _humanOverride;
    private static readonly HashSet<Stage> NeuralNetworkUnavailableStages = new HashSet<Stage>();

    internal static ControllerKind Resolve(Stage stage, Level level, int side)
    {
        if (stage == null || ConfigData.Configuration == null)
        {
            return ControllerKind.None;
        }

        EnsureOverrides();
        ControllerKind resolved;
        ControllerKind? explicitController = side == ConfigData.Configuration.BeeSide
            ? _beeOverride
            : side == ConfigData.Configuration.HumanSide
                ? _humanOverride
                : null;
        if (explicitController.HasValue)
        {
            resolved = explicitController.Value;
        }
        else
        {
            bool hasPlayer = level != null ? level.HasPlayer : stage.DoesUserHaveController;
            if (hasPlayer && stage.DoesUserHaveController && side == ConfigData.Configuration.UserSide)
            {
                resolved = ControllerKind.Player;
            }
            else if (stage.ActivateHiveMind && stage.ActivateBrains)
            {
                resolved = ControllerKind.NeuralNetwork;
            }
            else if (stage.ActivateHiveMind)
            {
                resolved = ControllerKind.HiveMind;
            }
            else
            {
                resolved = ControllerKind.None;
            }
        }

        if (resolved == ControllerKind.NeuralNetwork && NeuralNetworkUnavailableStages.Contains(stage))
        {
            return ControllerKind.HiveMind;
        }
        return resolved;
    }

    /// <summary>
    /// Early scene-load query that does not depend on ConfigData.Configuration being ready yet.
    /// Runtime installers use this to avoid missing explicit/default NN ownership before user data
    /// finishes loading. Full per-side resolution still happens through Resolve once configuration exists.
    /// </summary>
    internal static bool AnyNeuralNetworkRequested(Stage stage)
    {
        return AnyRequested(stage, ControllerKind.NeuralNetwork);
    }

    internal static bool AnyHiveMindRequested(Stage stage)
    {
        return AnyRequested(stage, ControllerKind.HiveMind);
    }

    internal static void SetNeuralNetworkAvailable(Stage stage, bool available)
    {
        if (stage == null)
        {
            return;
        }
        if (available)
        {
            NeuralNetworkUnavailableStages.Remove(stage);
        }
        else
        {
            NeuralNetworkUnavailableStages.Add(stage);
        }
    }

    internal static bool AnyNeuralNetwork(Stage stage)
    {
        return Any(stage, ControllerKind.NeuralNetwork);
    }

    internal static bool AnyHiveMind(Stage stage)
    {
        return Any(stage, ControllerKind.HiveMind);
    }

    internal static bool IsExternalExpert(ControllerKind controller)
    {
        return controller == ControllerKind.Player || controller == ControllerKind.HiveMind;
    }

    internal static string ExternalSourceName(ControllerKind controller)
    {
        switch (controller)
        {
            case ControllerKind.Player:
                return "human";
            case ControllerKind.HiveMind:
                return "hivemind";
            default:
                throw new ArgumentOutOfRangeException(nameof(controller), controller,
                    "Only human and Hive Mind controllers are external experts.");
        }
    }

    internal static ControllerKind ParseControllerForTests(string value)
    {
        return ParseController(value, "test");
    }

    internal static ControllerKind ResolveForTests(
        bool hasPlayer,
        bool userOwnsSide,
        bool activateHiveMind,
        bool activateBrains)
    {
        if (hasPlayer && userOwnsSide)
        {
            return ControllerKind.Player;
        }
        if (activateHiveMind && activateBrains)
        {
            return ControllerKind.NeuralNetwork;
        }
        if (activateHiveMind)
        {
            return ControllerKind.HiveMind;
        }
        return ControllerKind.None;
    }

    private static bool AnyRequested(Stage stage, ControllerKind expected)
    {
        if (stage == null)
        {
            return false;
        }
        EnsureOverrides();

        if (_beeOverride == expected || _humanOverride == expected)
        {
            return expected != ControllerKind.NeuralNetwork || !NeuralNetworkUnavailableStages.Contains(stage);
        }

        // Explicitly overriding both sides removes the serialized default from consideration.
        if (_beeOverride.HasValue && _humanOverride.HasValue)
        {
            return false;
        }

        if (expected == ControllerKind.NeuralNetwork)
        {
            return stage.ActivateHiveMind && stage.ActivateBrains &&
                   !NeuralNetworkUnavailableStages.Contains(stage);
        }
        if (expected == ControllerKind.HiveMind)
        {
            if (NeuralNetworkUnavailableStages.Contains(stage) &&
                ((_beeOverride ?? ControllerKind.NeuralNetwork) == ControllerKind.NeuralNetwork ||
                 (_humanOverride ?? ControllerKind.NeuralNetwork) == ControllerKind.NeuralNetwork))
            {
                return true;
            }
            return stage.ActivateHiveMind && !stage.ActivateBrains;
        }
        return false;
    }

    private static bool Any(Stage stage, ControllerKind expected)
    {
        if (stage == null)
        {
            return false;
        }
        if (ConfigData.Configuration == null)
        {
            return AnyRequested(stage, expected);
        }

        IReadOnlyList<Level> levels = stage.Levels;
        if (levels != null && levels.Count > 0)
        {
            for (int i = 0; i < levels.Count; i++)
            {
                Level level = levels[i];
                if (Resolve(stage, level, ConfigData.Configuration.BeeSide) == expected ||
                    Resolve(stage, level, ConfigData.Configuration.HumanSide) == expected)
                {
                    return true;
                }
            }
            return false;
        }

        return Resolve(stage, null, ConfigData.Configuration.BeeSide) == expected ||
               Resolve(stage, null, ConfigData.Configuration.HumanSide) == expected;
    }

    private static void EnsureOverrides()
    {
        if (_overridesInitialized)
        {
            return;
        }
        IReadOnlyList<string> args = Environment.GetCommandLineArgs();
        _beeOverride = ReadOverride(args, BeeControllerFlag);
        _humanOverride = ReadOverride(args, HumanControllerFlag);
        _overridesInitialized = true;
    }

    private static ControllerKind? ReadOverride(IReadOnlyList<string> args, string flag)
    {
        if (args == null)
        {
            return null;
        }
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(flag + " requires player, hivemind, or nn.");
                }
                return ParseController(args[i + 1], flag);
            }
            string prefix = flag + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ParseController(argument.Substring(prefix.Length), flag);
            }
        }
        return null;
    }

    private static ControllerKind ParseController(string raw, string flag)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (value)
        {
            case "player":
            case "human":
                return ControllerKind.Player;
            case "hivemind":
            case "hive-mind":
            case "hive":
                return ControllerKind.HiveMind;
            case "nn":
            case "neuralnetwork":
            case "neural-network":
                return ControllerKind.NeuralNetwork;
            case "none":
                return ControllerKind.None;
            default:
                throw new InvalidOperationException(
                    flag + " must be one of player, hivemind, nn, or none; got " + raw + ".");
        }
    }
}
