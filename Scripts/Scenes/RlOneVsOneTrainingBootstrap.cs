using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.UIComponents;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Configures the dedicated neural-network combat scene. The defaults preserve the original small
/// Wasp-vs-Gunship 1v1 proof, while standalone ML-Agents workers can override curriculum/environment
/// dimensions through command-line arguments passed with mlagents-learn --env-args.
/// </summary>
internal static class RlOneVsOneTrainingBootstrap
{
    internal const string TrainingSceneName = "RL 1v1 Training";
    internal const int TrainingLevelCount = 1;

    // Keep these original proof constants as the stable no-argument defaults. Runtime code uses the
    // Current* accessors below so command-line curriculum changes do not alter serialized scene state.
    internal const int TrainingTimeoutSeconds = RlOneVsOneTrainingOptions.DefaultEpisodeTimeoutSeconds;
    internal const int TrainingMapIndex = 2; // Reuse Titania's art/prefab plumbing, then resize it for this scene only.
    internal const float TrainingMapSize = RlOneVsOneTrainingOptions.DefaultMapSize;
    internal const float TrainingCameraSize = TrainingMapSize / 2f;
    internal const float SpawnRadius = TrainingMapSize / 4f;

    private const float AuthoredMapSize = 512f;
    private const float BorderThickness = 24f;
    private const float BorderHalfThickness = BorderThickness / 2f;
    private const float BorderOverhang = BorderThickness * 2f;

    // Stable default matchup retained for checkpoint compatibility and the no-argument proof.
    internal const ConfigData.ShipTypes BeeShipType = ConfigData.ShipTypes.Wasp;
    internal const ConfigData.ShipTypes HumanShipType = ConfigData.ShipTypes.Gunship;

    private static RlOneVsOneTrainingOptions _runtimeOptions;

    internal static bool IsDedicatedTrainingRuntime =>
        ShouldApply(SceneManager.GetActiveScene().name);

    internal static float CurrentHealthRatio => RuntimeOptions.HealthRatio;
    internal static float CurrentMapSize => RuntimeOptions.MapSize;
    internal static float CurrentCameraSize => CurrentMapSize / 2f;
    internal static float CurrentSpawnRadius => CurrentMapSize / 4f;
    internal static int CurrentTimeoutSeconds => RuntimeOptions.EpisodeTimeoutSeconds;
    internal static int CurrentShipsPerSide => RuntimeOptions.ShipsPerSide;

    private static RlOneVsOneTrainingOptions RuntimeOptions
    {
        get
        {
            if (_runtimeOptions == null)
            {
                _runtimeOptions = RlOneVsOneTrainingOptions.Parse(Environment.GetCommandLineArgs());
            }
            return _runtimeOptions;
        }
    }

    internal static bool ShouldApply(string sceneName)
    {
        return sceneName == TrainingSceneName;
    }

    internal static bool IsActiveFor(Stage stage)
    {
        return stage != null && stage.IsTrainingNueralNetwork && IsDedicatedTrainingRuntime;
    }

    /// <summary>
    /// Keep the visual training view in the Unity Editor and ordinary standalone players, but do not
    /// ask the gameplay layer to construct/render presentation work in a dedicated headless player.
    /// ML-Agents --no-graphics produces a null graphics device; batch-mode standalone execution is
    /// also treated as headless. Editor tests remain on the visible-training path regardless of how
    /// the Editor process itself was launched.
    /// </summary>
    internal static bool ShouldRenderTraining(bool isEditor, bool isBatchMode, GraphicsDeviceType graphicsDeviceType)
    {
        if (isEditor)
        {
            return true;
        }

        return !isBatchMode && graphicsDeviceType != GraphicsDeviceType.Null;
    }

    internal static ConfigData.ShipTypes GetShipTypeForSide(int side)
    {
        return GetShipTypeForSide(side, 0);
    }

    internal static ConfigData.ShipTypes GetShipTypeForSide(int side, int shipIndex)
    {
        if (side == ConfigData.Configuration.BeeSide)
        {
            return RuntimeOptions.GetBeeShipType(shipIndex);
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return RuntimeOptions.GetHumanShipType(shipIndex);
        }
        throw new ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
    }

    internal static Vector2 GetShipFormationOffset(int shipIndex)
    {
        int shipCount = CurrentShipsPerSide;
        if (shipIndex < 0 || shipIndex >= shipCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shipIndex));
        }
        if (shipCount == 1)
        {
            return Vector2.zero;
        }

        int columns = Mathf.CeilToInt(Mathf.Sqrt(shipCount));
        int rows = Mathf.CeilToInt((float)shipCount / columns);
        int row = shipIndex / columns;
        int column = shipIndex % columns;
        int itemsInRow = Mathf.Min(columns, shipCount - row * columns);

        // Scale formation spacing with the selected arena while capping it so a larger map does not
        // scatter a small team unnecessarily. The last partial row is centered independently.
        float spacing = Mathf.Clamp(CurrentMapSize / (Mathf.Max(columns, rows) + 4f), 1.5f, 6f);
        float x = (column - (itemsInRow - 1) * 0.5f) * spacing;
        float y = (row - (rows - 1) * 0.5f) * spacing;
        return new Vector2(x, y);
    }

    /// <summary>
    /// Reuses the normal Titania map object so all normal map ownership/pooling code stays intact,
    /// but resizes the playable area and its four trigger borders for this training process only.
    /// </summary>
    internal static void ConfigureTrainingMap(Map map)
    {
        if (map == null || map.SpriteRenderer == null)
        {
            return;
        }

        float mapSize = CurrentMapSize;
        float spawnRadius = CurrentSpawnRadius;
        map.SpriteRenderer.size = new Vector2(mapSize, mapSize);
        float scale = mapSize / AuthoredMapSize;
        map.SizeMultiplier = new Vector2(scale, scale);
        map.UserStartingPosition = new Vector2(0f, -spawnRadius);
        map.AIStartingPosition = new Vector2(0f, spawnRadius);

        float halfMap = mapSize / 2f;
        float borderCenter = halfMap + BorderHalfThickness;
        float longBorder = mapSize + BorderOverhang;
        MapBorder[] borders = map.GetComponentsInChildren<MapBorder>(true);
        for (int i = 0; i < borders.Length; i++)
        {
            Transform border = borders[i].transform;
            string borderName = border.name;
            Vector3 position = border.localPosition;
            Vector3 size = border.localScale;

            if (borderName.Contains("Top Border"))
            {
                position.x = 0f;
                position.y = borderCenter;
                size.x = longBorder;
                size.y = BorderThickness;
            }
            else if (borderName.Contains("Bottom Border"))
            {
                position.x = 0f;
                position.y = -borderCenter;
                size.x = longBorder;
                size.y = BorderThickness;
            }
            else if (borderName.Contains("Right Border"))
            {
                position.x = borderCenter;
                position.y = 0f;
                size.x = BorderThickness;
                size.y = longBorder;
            }
            else if (borderName.Contains("Left Border"))
            {
                position.x = -borderCenter;
                position.y = 0f;
                size.x = BorderThickness;
                size.y = longBorder;
            }
            else
            {
                continue;
            }

            border.localPosition = position;
            border.localScale = size;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyToLoadedTrainingScene()
    {
        if (!IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            Debug.LogError("RL 1v1 training scene loaded without a Stage.");
            return;
        }

        try
        {
            Apply(stage);
        }
        catch (ArgumentException exception)
        {
            // A typo in an --env-args flag must never silently train the default environment.
            stage.IsTrainingNueralNetwork = false;
            Debug.LogError($"Invalid RL training command-line configuration: {exception.Message}");
            if (!Application.isEditor)
            {
                Application.Quit(2);
            }
        }
    }

    internal static void Apply(Stage stage)
    {
        if (stage == null)
        {
            return;
        }

        RlOneVsOneTrainingOptions options = RuntimeOptions;

        // Training skips normal GameMenus.Setup(), while Level.SetupLevel() still expects the
        // serialized ActionBox hierarchy to exist. Preserve it but keep it inactive.
        if (stage.Menus == null && stage.UIManager != null)
        {
            stage.Menus = stage.UIManager.GetComponentInChildren<GameMenus>(true);
        }
        if (stage.Menus != null && stage.Menus.ActionBox == null && stage.Menus.SquadActionBoxUI != null)
        {
            stage.Menus.ActionBox = stage.Menus.SquadActionBoxUI.GetComponent<SquadActionBox>();
        }
        PreserveLegacySetupUi(stage);

        stage.IsTrainingHiveMind = false;
        stage.IsTrainingNueralNetwork = true;
        stage.ActivateHiveMind = false;
        stage.ActivateBrains = false; // The ML-Agents adapters own actions; the historical Brain path is dormant.
        stage.DoesUserHaveController = false;
        stage.UseFullyRandomSquads = true;
        stage.UseFullyRandomEnemySquads = false;
        stage.HasRandomizedOptions = false;
        stage.IsRendering = ShouldRenderTraining(Application.isEditor, Application.isBatchMode, SystemInfo.graphicsDeviceType);

        // Automated training skips AudioController.Setup(), so inherited scene audio flags must
        // not reach music or effect paths that expect the controller to have a bound Level.
        stage.ActivateAudio = false;
        stage.PlayMusic = false;

        stage.LevelCount = TrainingLevelCount;

        // The dedicated RL path always creates one explicit squad per side; CurrentShipsPerSide
        // controls the number of explicit FleetShips inside that squad.
        stage.GeneratedSquadCountOverride = 1;
        stage.GeneratedSquadCountMinimum = 0;

        stage.OverrideMapIndex = TrainingMapIndex;
        stage.TimeoutTime = options.EpisodeTimeoutSeconds;
        stage.InitialCommandDelay = 0;

        stage.OverrideBeeShipTypes = new List<ConfigData.ShipTypes>(options.BeeShipTypes);
        stage.OverrideHumanShipTypes = new List<ConfigData.ShipTypes>(options.HumanShipTypes);

        Debug.Log($"RL training configuration {options.Describe()}");

        RlOneVsOneTrainingRuntimeGuard guard = stage.GetComponent<RlOneVsOneTrainingRuntimeGuard>();
        if (guard == null)
        {
            guard = stage.gameObject.AddComponent<RlOneVsOneTrainingRuntimeGuard>();
        }
        guard.Configure(stage);
    }

    private static void PreserveLegacySetupUi(Stage stage)
    {
        if (stage.UIManager == null)
        {
            return;
        }

        Transform uiManagerTransform = stage.UIManager.transform;
        if (stage.UIElements != null)
        {
            for (int i = stage.UIElements.Count - 1; i >= 0; i--)
            {
                GameObject uiElement = stage.UIElements[i];
                if (uiElement == null)
                {
                    continue;
                }

                Transform elementTransform = uiElement.transform;
                if (elementTransform == uiManagerTransform ||
                    elementTransform.IsChildOf(uiManagerTransform) ||
                    uiManagerTransform.IsChildOf(elementTransform))
                {
                    uiElement.SetActive(false);
                    stage.UIElements.RemoveAt(i);
                }
            }
        }

        stage.UIManager.SetActive(false);
    }
}

/// <summary>
/// Dedicated presentation and arena guard for RL combat training. It runs after ordinary gameplay
/// FixedUpdate callbacks so the policy can use the normal ship movement code while still respecting
/// the configured training arena. This is deliberately scoped to the dedicated RL scene.
/// </summary>
[DefaultExecutionOrder(10000)]
internal sealed class RlOneVsOneTrainingRuntimeGuard : MonoBehaviour
{
    private Stage _stage;

    internal void Configure(Stage stage)
    {
        _stage = stage;
    }

    private void FixedUpdate()
    {
        if (!RlOneVsOneTrainingBootstrap.IsActiveFor(_stage))
        {
            return;
        }

        Level level = _stage.PrimaryLevel;
        if (level == null || level.State == null)
        {
            return;
        }

        List<Ship> ships = level.State.GetShips();
        for (int i = 0; i < ships.Count; i++)
        {
            ConstrainShipToArena(level, ships[i]);
        }
    }

    private void LateUpdate()
    {
        if (!RlOneVsOneTrainingBootstrap.IsActiveFor(_stage) || _stage.Camera == null || _stage.PrimaryLevel == null)
        {
            return;
        }

        _stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.CurrentCameraSize;
        Vector2 levelPosition = _stage.PrimaryLevel.GetPosition();
        _stage.Camera.transform.position = new Vector3(levelPosition.x, levelPosition.y, -10f);
    }

    private static void ConstrainShipToArena(Level level, Ship ship)
    {
        if (ship == null || ship.IsDead || ship.CanOverrideBounds || ship.Body == null)
        {
            return;
        }

        // Keep the complete rotated ship inside the map by reserving its largest half-dimension
        // on every side. This is slightly conservative for non-square ships but remains correct
        // for every heading without needing an expensive rotated-bounds calculation each step.
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

        if (position != clampedPosition)
        {
            Vector3 localPosition = ship.transform.localPosition;
            localPosition.x = clampedPosition.x;
            localPosition.y = clampedPosition.y;
            ship.transform.localPosition = localPosition;
            ship.Body.position = ship.transform.position;
            position = clampedPosition;
        }

        Vector2 velocity = ship.Body.linearVelocity;
        float fixedDeltaTime = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
        Vector2 projectedPosition = position + velocity * fixedDeltaTime;

        if (projectedPosition.x < minX && velocity.x < 0f)
        {
            velocity.x = (minX - position.x) / fixedDeltaTime;
        }
        else if (projectedPosition.x > maxX && velocity.x > 0f)
        {
            velocity.x = (maxX - position.x) / fixedDeltaTime;
        }

        if (projectedPosition.y < minY && velocity.y < 0f)
        {
            velocity.y = (minY - position.y) / fixedDeltaTime;
        }
        else if (projectedPosition.y > maxY && velocity.y > 0f)
        {
            velocity.y = (maxY - position.y) / fixedDeltaTime;
        }

        ship.Body.linearVelocity = velocity;
    }
}
