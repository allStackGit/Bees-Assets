using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    /// <summary>
    /// Stage-owned pool for the common static obstacle/background prefabs used by
    /// randomized and saved obstacle layouts. Authored obstacle-container prefabs
    /// keep their existing ownership because their child hierarchy can contain
    /// additional MapObject state.
    /// </summary>
    public class StaticObstaclePool : MonoBehaviour
    {
        private Stage _stage;
        private readonly Stack<StaticObstacle> _inactiveObstacles = new Stack<StaticObstacle>();
        private readonly HashSet<EntityId> _inactiveObstacleIds = new HashSet<EntityId>();
        private readonly Stack<GameObject> _inactiveBackgrounds = new Stack<GameObject>();
        private readonly HashSet<EntityId> _inactiveBackgroundIds = new HashSet<EntityId>();

        public static StaticObstaclePool GetOrCreate(Stage stage)
        {
            StaticObstaclePool pool = stage.GetComponent<StaticObstaclePool>();
            if (pool == null)
            {
                pool = stage.gameObject.AddComponent<StaticObstaclePool>();
            }
            pool._stage = stage;
            return pool;
        }

        public StaticObstacle GetObstacle(Transform parent)
        {
            StaticObstacle obstacle = null;
            while (_inactiveObstacles.Count > 0 && obstacle == null)
            {
                obstacle = _inactiveObstacles.Pop();
                if (obstacle != null)
                {
                    _inactiveObstacleIds.Remove(obstacle.GetEntityId());
                }
            }

            if (obstacle == null)
            {
                GameObject obstacleObject = Instantiate(_stage.Prefabs.ObstaclePrefab, transform);
                obstacle = obstacleObject.GetComponent<StaticObstacle>();
                obstacleObject.SetActive(false);
            }

            obstacle.ResetForReuse();
            obstacle.IsPooledStaticLayoutObstacle = true;
            obstacle.transform.SetParent(parent, false);
            obstacle.transform.localPosition = _stage.Prefabs.ObstaclePrefab.transform.localPosition;
            obstacle.transform.localRotation = _stage.Prefabs.ObstaclePrefab.transform.localRotation;
            obstacle.transform.localScale = _stage.Prefabs.ObstaclePrefab.transform.localScale;
            return obstacle;
        }

        public GameObject GetBackground(Transform parent)
        {
            GameObject background = null;
            while (_inactiveBackgrounds.Count > 0 && background == null)
            {
                background = _inactiveBackgrounds.Pop();
                if (background != null)
                {
                    _inactiveBackgroundIds.Remove(background.GetEntityId());
                }
            }

            if (background == null)
            {
                background = Instantiate(_stage.Prefabs.ObstacleBackgroundPrefab, transform);
                background.SetActive(false);
            }

            background.transform.SetParent(parent, false);
            background.transform.localPosition = _stage.Prefabs.ObstacleBackgroundPrefab.transform.localPosition;
            background.transform.localRotation = _stage.Prefabs.ObstacleBackgroundPrefab.transform.localRotation;
            background.transform.localScale = _stage.Prefabs.ObstacleBackgroundPrefab.transform.localScale;
            background.SetActive(true);
            return background;
        }

        public void ReleaseObstacle(StaticObstacle obstacle)
        {
            if (obstacle == null || !_inactiveObstacleIds.Add(obstacle.GetEntityId()))
            {
                return;
            }

            obstacle.gameObject.SetActive(false);
            obstacle.IsPooledStaticLayoutObstacle = false;
            obstacle.ResetForReuse();
            obstacle.transform.SetParent(transform, false);
            _inactiveObstacles.Push(obstacle);
        }

        public void ReleaseBackground(GameObject background)
        {
            if (background == null || !_inactiveBackgroundIds.Add(background.GetEntityId()))
            {
                return;
            }

            background.SetActive(false);
            background.transform.SetParent(transform, false);
            _inactiveBackgrounds.Push(background);
        }
    }
}
