using Assets.Scripts.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Levels
{
    internal static class TitaniaRouteState
    {
        private const string ProgressProperty = "TitaniaOpenedBarrierPositions";
        private const string LegacyKeyPrefix = "bees.titania.route.";
        private static readonly HashSet<string> OpenedBarrierPositions = new HashSet<string>();
        private static bool _loaded;

        internal static void BeginMinesweeper()
        {
            EnsureLoaded();
            OpenedBarrierPositions.Clear();
            SaveProgress();
        }

        internal static void RecordOpenedBarrier(Vector2 localPosition)
        {
            EnsureLoaded();
            if (OpenedBarrierPositions.Add(ToKey(localPosition)))
            {
                SaveProgress();
            }
        }

        internal static bool WasBarrierOpened(Vector2 localPosition)
        {
            EnsureLoaded();
            return OpenedBarrierPositions.Contains(ToKey(localPosition));
        }

        internal static string AddToPlayerProgressJson(string userProgressJson)
        {
            EnsureLoaded();
            JObject progress = JObject.Parse(userProgressJson);
            progress[ProgressProperty] = new JArray(OpenedBarrierPositions.OrderBy(key => key));
            return progress.ToString(Formatting.None);
        }

        private static void EnsureLoaded()
        {
            if (_loaded || ConfigData.UserProgressData == null || !ConfigData.IsUserProgressDataLoaded)
            {
                return;
            }

            _loaded = true;
            OpenedBarrierPositions.Clear();
            if (ConfigData.UserProgressData.GetDataFile().GetJsonObject() is JObject progress &&
                progress[ProgressProperty] is JArray storedRoute)
            {
                foreach (string key in storedRoute.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(key)) OpenedBarrierPositions.Add(key);
                }
                return;
            }

            // One-time migration for profiles that completed Titania I before this field existed.
            string legacy = PlayerPrefs.GetString(LegacyKeyPrefix + ConfigData.GetUserId(), string.Empty);
            foreach (string key in legacy.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(key)) OpenedBarrierPositions.Add(key.Trim());
            }
        }

        private static void SaveProgress()
        {
            ConfigData.UserProgressData?.Save();
        }

        private static string ToKey(Vector2 position)
        {
            return $"{Mathf.RoundToInt(position.x * 10f)},{Mathf.RoundToInt(position.y * 10f)}";
        }
    }

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
                ConfigData.UserProgressData == null || ConfigData.Configuration == null || ConfigData.LevelOptions == null)
            {
                return;
            }

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide, ConfigData.GameModes.Campaign);
            if (missionId == 8 && ConfigData.LevelOptions.Obstacles == "Bee-noculars")
            {
                ConfigData.LevelOptions.Obstacles = "Minesweeper";
            }
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign) return;

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || level.CurrentLevelOptions == null || level.CurrentLevelOptions.Id != 8 ||
                    level.Map == null || level.Pathfinder == null ||
                    level.gameObject.GetComponent<TitaniaMazeAppliedMarker>() != null)
                {
                    continue;
                }

                MapObject[] demolitionObjects = level.Map.transform.GetComponentsInChildren<MapObject>(true);
                if (demolitionObjects.Length == 0) continue;

                List<Obstacle> barriers = level.Map.transform.GetComponentsInChildren<Obstacle>(true)
                    .Where(obstacle => obstacle != null && !obstacle.IsDead).ToList();
                HashSet<Transform> assigned = new HashSet<Transform>();

                foreach (MapObject demolitionObject in demolitionObjects)
                {
                    if (demolitionObject == null) continue;
                    Obstacle barrier = barriers
                        .Where(obstacle => !assigned.Contains(obstacle.transform))
                        .OrderBy(obstacle => ((Vector2)obstacle.transform.position -
                                             (Vector2)demolitionObject.transform.position).sqrMagnitude)
                        .FirstOrDefault();
                    if (barrier != null)
                    {
                        assigned.Add(barrier.transform);
                        if (TitaniaRouteState.WasBarrierOpened(barrier.transform.localPosition)) barrier.Kill();
                    }
                    demolitionObject.gameObject.SetActive(false);
                }

                level.CurrentLevelOptions.Obstacles = "Bee-noculars";
                level.gameObject.AddComponent<TitaniaMazeAppliedMarker>();
            }
        }
    }

    internal sealed class TitaniaMazeAppliedMarker : MonoBehaviour { }
}
