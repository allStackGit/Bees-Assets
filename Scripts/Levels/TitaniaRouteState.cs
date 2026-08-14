using Assets.Scripts.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Levels
{
    internal static class TitaniaRouteState
    {
        private const string StoragePrefix = "bees.titania.route.";
        private static readonly HashSet<Vector2Int> OpenedBarrierPositions = new HashSet<Vector2Int>();
        private static string _loadedStorageKey;

        internal static void BeginMinesweeper()
        {
            _loadedStorageKey = GetStorageKey();
            OpenedBarrierPositions.Clear();
            PlayerPrefs.DeleteKey(_loadedStorageKey);
            PlayerPrefs.Save();
        }

        internal static void RecordOpenedBarrier(Vector2 localPosition)
        {
            EnsureLoaded();
            if (!OpenedBarrierPositions.Add(ToKey(localPosition)))
            {
                return;
            }

            string serialized = string.Join(";", OpenedBarrierPositions
                .OrderBy(position => position.x)
                .ThenBy(position => position.y)
                .Select(position => $"{position.x},{position.y}"));
            PlayerPrefs.SetString(_loadedStorageKey, serialized);
            PlayerPrefs.Save();
        }

        internal static bool WasBarrierOpened(Vector2 localPosition)
        {
            EnsureLoaded();
            return OpenedBarrierPositions.Contains(ToKey(localPosition));
        }

        private static void EnsureLoaded()
        {
            string storageKey = GetStorageKey();
            if (_loadedStorageKey == storageKey)
            {
                return;
            }

            _loadedStorageKey = storageKey;
            OpenedBarrierPositions.Clear();
            string serialized = PlayerPrefs.GetString(storageKey, string.Empty);
            foreach (string entry in serialized.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] values = entry.Split(',');
                if (values.Length == 2 && int.TryParse(values[0], out int x) && int.TryParse(values[1], out int y))
                {
                    OpenedBarrierPositions.Add(new Vector2Int(x, y));
                }
            }
        }

        private static string GetStorageKey()
        {
            return StoragePrefix + ConfigData.GetUserId();
        }

        private static Vector2Int ToKey(Vector2 position)
        {
            return new Vector2Int(
                Mathf.RoundToInt(position.x * 10f),
                Mathf.RoundToInt(position.y * 10f));
        }
    }

    /// <summary>
    /// Beenoculars uses the same maze as Minesweeper after the demolition sequence. The authored
    /// Beenoculars prefab currently has every removable wall already gone, so load the intact maze
    /// and remove only the wall positions actually opened during the immediately preceding mission.
    /// </summary>
    [DefaultExecutionOrder(1500)]
    internal sealed class TitaniaMazeContinuityGuard : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= PrepareLevelOptions;
            SceneManager.sceneLoaded += PrepareLevelOptions;

            GameObject host = new GameObject("Titania Maze Continuity Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<TitaniaMazeContinuityGuard>();
        }

        private static void PrepareLevelOptions(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Space" || ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null ||
                ConfigData.LevelOptions == null)
            {
                return;
            }

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (missionId == 8 && ConfigData.LevelOptions.Obstacles == "Bee-noculars")
            {
                ConfigData.LevelOptions.Obstacles = "Minesweeper";
            }
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || level.CurrentLevelOptions == null || level.CurrentLevelOptions.Id != 8 ||
                    level.Map == null || level.Pathfinder == null ||
                    level.gameObject.GetComponent<TitaniaMazeAppliedMarker>() != null)
                {
                    continue;
                }

                MapObject[] demolitionObjects = level.Map.transform.GetComponentsInChildren<MapObject>(true);
                if (demolitionObjects.Length == 0)
                {
                    continue;
                }

                List<Obstacle> barriers = level.Map.transform.GetComponentsInChildren<Obstacle>(true)
                    .Where(obstacle => obstacle != null && !obstacle.IsDead)
                    .ToList();
                HashSet<Transform> assignedBarriers = new HashSet<Transform>();

                foreach (MapObject demolitionObject in demolitionObjects)
                {
                    if (demolitionObject == null)
                    {
                        continue;
                    }

                    Obstacle nearestBarrier = barriers
                        .Where(obstacle => !assignedBarriers.Contains(obstacle.transform))
                        .OrderBy(obstacle =>
                            ((Vector2)obstacle.transform.position - (Vector2)demolitionObject.transform.position).sqrMagnitude)
                        .FirstOrDefault();
                    if (nearestBarrier != null)
                    {
                        assignedBarriers.Add(nearestBarrier.transform);
                        if (TitaniaRouteState.WasBarrierOpened(nearestBarrier.transform.localPosition))
                        {
                            // Kill through the normal obstacle lifecycle so the Pathfinder removes
                            // the wall instead of merely hiding its renderer/collider.
                            nearestBarrier.Kill();
                        }
                    }

                    // Titania II inherits the resulting openings, not the demolition objects.
                    demolitionObject.gameObject.SetActive(false);
                }

                level.CurrentLevelOptions.Obstacles = "Bee-noculars";
                level.gameObject.AddComponent<TitaniaMazeAppliedMarker>();
            }
        }
    }

    internal sealed class TitaniaMazeAppliedMarker : MonoBehaviour
    {
    }
}
