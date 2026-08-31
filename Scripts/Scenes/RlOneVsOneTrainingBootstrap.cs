using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.UIComponents;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Configures the first neural-network proof scene as one small, repeatable 1v1 battle.
/// This is intentionally narrow: no Hive Mind commands, no randomized environment dimensions,
/// no mining, and exactly one armed primary-fleet ship per side.
/// </summary>
internal static class RlOneVsOneTrainingBootstrap
{
    internal const string TrainingSceneName = "RL 1v1 Training";
    internal const int TrainingLevelCount = 1;
    internal const int TrainingTimeoutSeconds = 120;
    internal const int TrainingMapIndex = 2; // Reuse Titania's art/prefab plumbing, then resize it for this scene only.
    internal const float TrainingMapSize = 120f;
    internal const float TrainingCameraSize = 60f;
    internal const float SpawnRadius = 30f;

    private const float AuthoredMapSize = 512f;
    private const float BorderThickness = 24f;
    private const float BorderHalfThickness = BorderThickness / 2f;
    private const float BorderOverhang = BorderThickness * 2f;

    // Fixed first proof matchup. Balance is not required because TSV shaping distinguishes
    // better and worse losing behavior while the much larger terminal reward still favors wins.
    internal const ConfigData.ShipTypes BeeShipType = ConfigData.ShipTypes.Wasp;
    internal const ConfigData.ShipTypes HumanShipType = ConfigData.ShipTypes.Gunship;

    internal static bool IsDedicatedTrainingRuntime =>
        ShouldApply(SceneManager.GetActiveScene().name);

    internal static bool ShouldApply(string sceneName)
    {
        return sceneName == TrainingSceneName;
    }

    internal static bool IsActiveFor(Stage stage)
    {
        return stage != null && stage.IsTrainingNueralNetwork && IsDedicatedTrainingRuntime;
    }

    internal static ConfigData.ShipTypes GetShipTypeForSide(int side)
    {
        if (side == ConfigData.Configuration.BeeSide)
        {
            return BeeShipType;
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return HumanShipType;
        }
        throw new System.ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
    }

    /// <summary>
    /// Reuses the normal Titania map object so all normal map ownership/pooling code stays intact,
    /// but shrinks the playable area and its four trigger borders to 120x120 for this scene only.
    /// </summary>
    internal static void ConfigureTrainingMap(Map map)
    {
        if (map == null || map.SpriteRenderer == null)
        {
            return;
        }

        map.SpriteRenderer.size = new Vector2(TrainingMapSize, TrainingMapSize);
        float scale = TrainingMapSize / AuthoredMapSize;
        map.SizeMultiplier = new Vector2(scale, scale);
        map.UserStartingPosition = new Vector2(0f, -SpawnRadius);
        map.AIStartingPosition = new Vector2(0f, SpawnRadius);

        float halfMap = TrainingMapSize / 2f;
        float borderCenter = halfMap + BorderHalfThickness;
        float longBorder = TrainingMapSize + BorderOverhang;
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

        Apply(stage);
    }

    internal static void Apply(Stage stage)
    {
        if (stage == null)
        {
            return;
        }

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
        stage.ActivateBrains = false; // The new RL controller will own actions; the historical Brain path is dormant.
        stage.DoesUserHaveController = false;
        stage.UseFullyRandomSquads = true;
        stage.UseFullyRandomEnemySquads = false;
        stage.HasRandomizedOptions = false;
        stage.IsRendering = true;

        // Automated training skips AudioController.Setup(), so inherited scene audio flags must
        // not reach music or effect paths that expect the controller to have a bound Level.
        stage.ActivateAudio = false;
        stage.PlayMusic = false;

        stage.LevelCount = TrainingLevelCount;

        // This scene was copied from the legacy Hive Mind training scene and can retain a non-zero
        // serialized minimum. The dedicated 1v1 proof must normalize both ends of the range so
        // StageConfigOptions never receives an invalid RandomInt range and always generates one squad.
        stage.GeneratedSquadCountOverride = 1;
        stage.GeneratedSquadCountMinimum = 0;

        stage.OverrideMapIndex = TrainingMapIndex;
        stage.TimeoutTime = TrainingTimeoutSeconds;
        stage.InitialCommandDelay = 0;

        stage.OverrideBeeShipTypes = new List<ConfigData.ShipTypes> { BeeShipType };
        stage.OverrideHumanShipTypes = new List<ConfigData.ShipTypes> { HumanShipType };

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
/// Dedicated presentation and arena guard for the first RL proof. It runs after ordinary gameplay
/// FixedUpdate callbacks so the policy can use the normal ship movement code while still respecting
/// the small training arena. This is deliberately scoped to the dedicated RL scene.
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

        // Fish Tank normally zooms all the way out to Map.MaxZoom. The dedicated proof instead
        // keeps the complete 120-unit arena visible at a useful scale, including after resets.
        _stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.TrainingCameraSize;
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
