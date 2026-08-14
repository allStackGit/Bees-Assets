using Assets.Scripts.Entities;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Levels
{
    internal static class TitaniaRouteState
    {
        private static readonly HashSet<Vector2Int> OpenedBarrierPositions = new HashSet<Vector2Int>();

        internal static void BeginMinesweeper()
        {
            OpenedBarrierPositions.Clear();
        }

        internal static void RecordOpenedBarrier(Vector2 localPosition)
        {
            OpenedBarrierPositions.Add(ToKey(localPosition));
        }

        internal static bool WasBarrierOpened(Vector2 localPosition)
        {
            return OpenedBarrierPositions.Contains(ToKey(localPosition));
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
