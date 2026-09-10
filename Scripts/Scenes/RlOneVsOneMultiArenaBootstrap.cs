using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Opt-in process-local arena parallelism for the dedicated RL training scene. The existing training
/// bootstrap remains the source of truth for every gameplay/training setting; this component only
/// raises Stage.LevelCount and gives the additional Levels non-overlapping world-space origins.
/// </summary>
[DefaultExecutionOrder(-9999)]
internal sealed class RlOneVsOneMultiArenaBootstrap : MonoBehaviour
{
    internal const string ArenasPerEnvironmentFlag = "--bees-rl-arenas-per-env";
    internal const int DefaultArenasPerEnvironment = 1;
    internal const int MaximumArenasPerEnvironment = 16;

    private Stage _stage;
    private bool _applied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (!RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            Debug.LogError("RL multi-arena bootstrap could not find the training Stage.");
            return;
        }

        RlOneVsOneMultiArenaBootstrap bootstrap = stage.GetComponent<RlOneVsOneMultiArenaBootstrap>();
        if (bootstrap == null)
        {
            bootstrap = stage.gameObject.AddComponent<RlOneVsOneMultiArenaBootstrap>();
        }
        bootstrap._stage = stage;
    }

    internal static int ReadRequestedArenaCount(string[] args)
    {
        if (args == null)
        {
            return DefaultArenasPerEnvironment;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            string value = null;
            if (argument.Equals(ArenasPerEnvironmentFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                {
                    throw new ArgumentException($"{ArenasPerEnvironmentFlag} requires a value.");
                }
                value = args[++i];
            }
            else
            {
                string prefix = ArenasPerEnvironmentFlag + "=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Substring(prefix.Length);
                }
            }

            if (value == null)
            {
                continue;
            }

            if (!int.TryParse(value, out int count) || count < 1 || count > MaximumArenasPerEnvironment)
            {
                throw new ArgumentException(
                    $"{ArenasPerEnvironmentFlag} must be a whole number between 1 and {MaximumArenasPerEnvironment}.");
            }
            return count;
        }

        return DefaultArenasPerEnvironment;
    }

    private void Update()
    {
        if (_applied || _stage == null || !ConfigData.AreAllSettingsLoaded)
        {
            return;
        }

        // RlOneVsOneTrainingStartupGate executes at -10000 and applies all normal RL settings first.
        // This component runs immediately afterwards, still before the ordinary Scene.Update path can
        // finalize the Stage and spawn Levels.
        if (!RlOneVsOneTrainingBootstrap.IsActiveFor(_stage))
        {
            return;
        }

        _applied = true;
        enabled = false;

        try
        {
            int arenaCount = ReadRequestedArenaCount(Environment.GetCommandLineArgs());
            _stage.LevelCount = arenaCount;
            if (arenaCount > 1)
            {
                RlOneVsOneTrainingOptions options = RlOneVsOneTrainingOptions.Parse(Environment.GetCommandLineArgs());
                _stage.LevelLayouts[arenaCount] = BuildLayout(arenaCount, options.MapSizeMaximum);
            }
            Debug.Log($"RL training arenas_per_environment={arenaCount}");
        }
        catch (ArgumentException exception)
        {
            _stage.IsTrainingNueralNetwork = false;
            Debug.LogError($"Invalid RL multi-arena configuration: {exception.Message}");
            if (!Application.isEditor)
            {
                Application.Quit(2);
            }
        }
    }

    internal static Vector2[] BuildLayout(int arenaCount, float maximumMapSize)
    {
        if (arenaCount < 1 || arenaCount > MaximumArenasPerEnvironment)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaCount));
        }

        Vector2[] positions = new Vector2[arenaCount];
        if (arenaCount == 1)
        {
            positions[0] = Vector2.zero;
            return positions;
        }

        int columns = Mathf.CeilToInt(Mathf.Sqrt(arenaCount));
        int rows = Mathf.CeilToInt((float)arenaCount / columns);
        float spacing = Mathf.Max(256f, maximumMapSize + 128f);
        float centerX = (columns - 1) * 0.5f;
        float centerY = (rows - 1) * 0.5f;

        for (int index = 0; index < arenaCount; index++)
        {
            int row = index / columns;
            int column = index % columns;
            positions[index] = new Vector2(
                (column - centerX) * spacing,
                (row - centerY) * spacing);
        }
        return positions;
    }

    private void FixedUpdate()
    {
        if (!_applied || _stage == null || !RlOneVsOneTrainingBootstrap.IsActiveFor(_stage))
        {
            return;
        }

        // The legacy runtime guard already constrains PrimaryLevel. Handle only the additional Levels
        // here so the single-arena path remains byte-for-byte equivalent in its per-step work.
        IReadOnlyList<Level> levels = _stage.Levels;
        for (int levelIndex = 1; levelIndex < levels.Count; levelIndex++)
        {
            Level level = levels[levelIndex];
            if (level == null || level.State == null)
            {
                continue;
            }

            List<Ship> ships = level.State.GetShips();
            for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
            {
                ConstrainShipToArena(level, ships[shipIndex]);
            }
        }
    }

    private static void ConstrainShipToArena(Level level, Ship ship)
    {
        if (ship == null || ship.IsDead || ship.CanOverrideBounds || ship.Body == null)
        {
            return;
        }

        float shipExtent = Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight());
        float minX = level.MinX + shipExtent;
        float maxX = level.MaxX - shipExtent;
        float minY = level.MinY + shipExtent;
        float maxY = level.MaxY - shipExtent;
        if (minX > maxX || minY > maxY)
        {
            ship.Body.linearVelocity = Vector2.zero;
            return;
        }

        Vector2 position = ship.GetPosition();
        Vector2 clampedPosition = new Vector2(
            Mathf.Clamp(position.x, minX, maxX),
            Mathf.Clamp(position.y, minY, maxY));
        if (position == clampedPosition)
        {
            return;
        }

        Vector3 localPosition = ship.transform.localPosition;
        localPosition.x = clampedPosition.x;
        localPosition.y = clampedPosition.y;
        ship.transform.localPosition = localPosition;
        ship.Body.position = clampedPosition;

        Vector2 velocity = ship.Body.linearVelocity;
        if ((Mathf.Approximately(clampedPosition.x, minX) && velocity.x < 0f) ||
            (Mathf.Approximately(clampedPosition.x, maxX) && velocity.x > 0f))
        {
            velocity.x = 0f;
        }
        if ((Mathf.Approximately(clampedPosition.y, minY) && velocity.y < 0f) ||
            (Mathf.Approximately(clampedPosition.y, maxY) && velocity.y > 0f))
        {
            velocity.y = 0f;
        }
        ship.Body.linearVelocity = velocity;
    }
}