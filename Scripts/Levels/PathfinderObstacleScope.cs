using Assets.Scripts.Entities;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Keeps Unity-facing obstacle discovery and coordinate conversion scoped to the Level
    /// that owns the operation. Physics2D collider APIs operate in world space, while
    /// gameplay/pathfinding coordinates are Level-local.
    /// </summary>
    public static class PathfinderObstacleScope
    {
        public static GameObject[] GetActiveObstacleObjects(Level level)
        {
            if (level?.Map?.Transform == null)
            {
                return new GameObject[0];
            }

            return level.Map.Transform
                .GetComponentsInChildren<Obstacle>(false)
                .Where(obstacle => obstacle != null)
                .Select(obstacle => obstacle.gameObject)
                .ToArray();
        }

        public static Vector2 WorldToLevel(Level level, Vector2 worldPoint)
        {
            return level.Map.Transform.InverseTransformPoint(worldPoint);
        }

        public static Vector2 LevelToWorld(Level level, Vector2 levelPoint)
        {
            return level.Map.Transform.TransformPoint(levelPoint);
        }
    }
}
