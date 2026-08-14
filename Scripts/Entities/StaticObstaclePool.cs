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
        private readonly HashSet<StaticObstacle> _inactiveObstacleSet = new HashSet<StaticObstacle>();
        private readonly Stack<GameObject> _inactiveBackgrounds = new Stack<GameObject>();
        private readonly HashSet<GameObject> _inactiveBackgroundSet = new HashSet<GameObject>();

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
                    _inactiveObstacleSet.Remove(obstacle);
                }
            }

            if (obstacle == null)
            {
                GameObject obstacleObject = Instantiate(_stage.Prefabs.ObstaclePrefab, transform);
                obstacle = obstacleObject.GetComponent<StaticObstacle>();
                obstacleObject.SetActive(false);
            }

            obstacle.ResetForReuse();
            obstacle.transform.SetParent(parent, false);
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
                    _inactiveBackgroundSet.Remove(background);
                }
            }

            if (background == null)
            {
                background = Instantiate(_stage.Prefabs.ObstacleBackgroundPrefab, transform);
                background.SetActive(false);
            }

            background.transform.SetParent(parent, false);
            background.SetActive(true);
            return background;
        }

        public void ReleaseObstacle(StaticObstacle obstacle)
        {
            if (obstacle == null || !_inactiveObstacleSet.Add(obstacle))
            {
                return;
            }

            obstacle.gameObject.SetActive(false);
            obstacle.ResetForReuse();
            obstacle.transform.SetParent(transform, false);
            _inactiveObstacles.Push(obstacle);
        }

        public void ReleaseBackground(GameObject background)
        {
            if (background == null || !_inactiveBackgroundSet.Add(background))
            {
                return;
            }

            background.SetActive(false);
            background.transform.SetParent(transform, false);
            _inactiveBackgrounds.Push(background);
        }
    }
}
