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

    private static string[] _cachedArgs;
    private static ControllerKind? _beeOverride;
    private static ControllerKind? _humanOverride;

    internal static ControllerKind Resolve(Stage stage, Level level, int side)
    {
        if (stage == null || ConfigData.Configuration == null)
        {
            return ControllerKind.None;
        }

        EnsureOverrides(Environment.GetCommandLineArgs());
        ControllerKind? explicitController = side == ConfigData.Configuration.BeeSide
            ? _beeOverride
            : side == ConfigData.Configuration.HumanSide
                ? _humanOverride
                : null;
        if (explicitController.HasValue)
        {
            return explicitController.Value;
        }

        bool hasPlayer = level != null ? level.HasPlayer : stage.DoesUserHaveController;
        if (hasPlayer && stage.DoesUserHaveController && side == ConfigData.Configuration.UserSide)
        {
            return ControllerKind.Player;
        }

        if (stage.ActivateHiveMind && stage.ActivateBrains)
        {
            return ControllerKind.NeuralNetwork;
        }
        if (stage.ActivateHiveMind)
        {
            return ControllerKind.HiveMind;
        }
        return ControllerKind.None;
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

    private static bool Any(Stage stage, ControllerKind expected)
    {
        if (stage == null || ConfigData.Configuration == null)
        {
            return false;
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

    private static void EnsureOverrides(IReadOnlyList<string> args)
    {
        string[] snapshot = args as string[];
        if (ReferenceEquals(snapshot, _cachedArgs))
        {
            return;
        }

        _cachedArgs = snapshot;
        _beeOverride = ReadOverride(args, BeeControllerFlag);
        _humanOverride = ReadOverride(args, HumanControllerFlag);
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
