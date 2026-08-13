using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
        private const int StaticDynamicLayerFrame = -2;

        public void InitializeMap()
        {
            float start = Time.realtimeSinceStartup;

            if (!Level.Stage.PathfinderGrids.ContainsKey((Width, Height)))
            {
                _grid = new Grid(Width, Height, this);
                Level.Stage.PathfinderGrids.Add((Width, Height), _grid);
            }
            else
            {
                _grid = Level.Stage.PathfinderGrids.GetValueOrDefault((Width, Height));
                _grid.Pathfinder = this;
                _grid.Reset();
            }

            GameObject[] obstacles = PathfinderObstacleScope.GetActiveObstacleObjects(Level);

            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                ObstaclePoints[i] = new List<int[][]>();
            }

            for (int i = 0; i < obstacles.Length; i++)
            {
                GameObject obstacleObject = obstacles[i];
                Obstacle obstacle = obstacleObject.GetComponent<Obstacle>();
                if (obstacle == null)
                {
                    continue;
                }

                try
                {
                    obstacle.Setup(Level);
                }
                catch (Exception e)
                {
                    Debug.Log($"Found {obstacleObject.name}: {obstacle?.Name}");
                    throw e;
                }

                obstacle.MapPointsIndex = AddObstacle(obstacle);
                ObstaclePoints[0][obstacle.MapPointsIndex] = ShouldBakeStaticObstacle(obstacle)
                    ? GetObstaclePoints(obstacle, 0, 0)
                    : new int[][] { };

                foreach (int[] point in ObstaclePoints[0][obstacle.MapPointsIndex])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        _grid.Nodes[point[0]][point[1]].Clearance = 0;
                        _grid.Nodes[point[0]][point[1]].OriginalClearance = 0;
                    }
                    else if (obstacle.ObstacleType != ConfigData.ObstacleTypes.MapBorder)
                    {
                        Debug.Log($"Invalid indexes: {point[0]}, {point[1]}");
                    }
                }
            }

            CalculateClearance(_grid.Nodes, 0, _grid.Width, 0, _grid.Height, int.MaxValue, false);
            _totalNodes = Width * Height;
            _baseClearance = new int[_totalNodes];
            _dynamicClearance = new int[_totalNodes];
            _threadClearance = new int[ConfigData.MaxThreads][];
            _costToHere = new int[ConfigData.MaxThreads][];
            _totalCost = new int[ConfigData.MaxThreads][];
            _heuristicCost = new int[ConfigData.MaxThreads][];
            _previousIndex = new int[ConfigData.MaxThreads][];
            _openStamp = new int[ConfigData.MaxThreads][];
            _closedStamp = new int[ConfigData.MaxThreads][];
            _searchStamp = new int[ConfigData.MaxThreads];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    _baseClearance[ToIndex(x, y)] = _grid.Nodes[x][y].Clearance;
                }
            }
            _staticSignedClearance = BuildStaticSignedClearance(_baseClearance);
            Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);
            _dynamicLayerFrame = StaticDynamicLayerFrame;

            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                ObstaclePoints[i] = ObstaclePoints[0].ToList();
                _threadClearance[i] = new int[_totalNodes];
                _costToHere[i] = new int[_totalNodes];
                _totalCost[i] = new int[_totalNodes];
                _heuristicCost[i] = new int[_totalNodes];
                _previousIndex[i] = new int[_totalNodes];
                _openStamp[i] = new int[_totalNodes];
                _closedStamp[i] = new int[_totalNodes];
                Array.Copy(_baseClearance, _threadClearance[i], _totalNodes);

                int nodeCount = 0;
                GridNodes[i] = new MapNode[_grid.Width][];
                for (int x = 0; x < _grid.Width; x++)
                {
                    GridNodes[i][x] = new MapNode[_grid.Height];
                    for (int y = 0; y < _grid.Height; y++)
                    {
                        MapNode original = _grid.Nodes[x][y];
                        MapNode node = new MapNode(x, y, _grid, nodeCount++);
                        node.Clearance = original.Clearance;
                        node.OriginalClearance = original.Clearance;
                        node.CostToHere = int.MaxValue;
                        node.TotalCost = int.MaxValue;
                        node.PreviousNode = MapNode.NullNode;
                        GridNodes[i][x][y] = node;
                    }
                }
            }

            float end = (Time.realtimeSinceStartup - start) * 1000;
        }

        public int AddObstacle(Obstacle obstacle)
        {
            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                ObstaclePoints[i].Add(new int[][] { });
            }

            if (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid && !HasMovingObstacles)
            {
                HasMovingObstacles = true;
            }
            return ObstaclePoints[0].Count - 1;
        }

        public int[][] GetObstaclePoints(Obstacle obstacle, float xVelocity, float yVelocity)
        {
            Collider2D collider = obstacle.ClearanceMappingCollider != null
                ? obstacle.ClearanceMappingCollider
                : obstacle.Collider;

            Bounds bounds = collider.bounds;
            float speedPaddingX = Mathf.Abs(xVelocity) * 2.5f;
            float speedPaddingY = Mathf.Abs(yVelocity) * 2.5f;
            bounds.Expand(new Vector3(speedPaddingX * 2f, speedPaddingY * 2f, 0));

            Vector2Int min = ConvertToMapCoordinates(
                PathfinderObstacleScope.WorldToLevel(Level, new Vector2(bounds.min.x, bounds.max.y)));
            Vector2Int max = ConvertToMapCoordinates(
                PathfinderObstacleScope.WorldToLevel(Level, new Vector2(bounds.max.x, bounds.min.y)));
            int startX = Mathf.Clamp(Mathf.Min(min.x, max.x), 0, _grid.MaxX);
            int endX = Mathf.Clamp(Mathf.Max(min.x, max.x), 0, _grid.MaxX);
            int startY = Mathf.Clamp(Mathf.Min(min.y, max.y), 0, _grid.MaxY);
            int endY = Mathf.Clamp(Mathf.Max(min.y, max.y), 0, _grid.MaxY);

            HashSet<int> pointSet = new HashSet<int>();
            List<int[]> points = new List<int[]>();

            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    if (DoesColliderTouchNode(collider, x, y, xVelocity, yVelocity))
                    {
                        int key = (y * _grid.Width) + x;
                        if (pointSet.Add(key))
                        {
                            points.Add(new int[] { x, y });
                        }
                    }
                }
            }

            return points.ToArray();
        }

        private bool DoesColliderTouchNode(Collider2D collider, int x, int y, float xVelocity, float yVelocity)
        {
            Vector2 center = ConvertToLevelCoordinates(x, y);
            float halfCell = Scale * 0.5f;
            Vector2 worldCenter = PathfinderObstacleScope.LevelToWorld(Level, center);
            Vector2 worldBottomLeft = PathfinderObstacleScope.LevelToWorld(Level, new Vector2(center.x - halfCell, center.y - halfCell));
            Vector2 worldTopLeft = PathfinderObstacleScope.LevelToWorld(Level, new Vector2(center.x - halfCell, center.y + halfCell));
            Vector2 worldBottomRight = PathfinderObstacleScope.LevelToWorld(Level, new Vector2(center.x + halfCell, center.y - halfCell));
            Vector2 worldTopRight = PathfinderObstacleScope.LevelToWorld(Level, new Vector2(center.x + halfCell, center.y + halfCell));

            if (collider.OverlapPoint(worldCenter) ||
                collider.OverlapPoint(worldBottomLeft) ||
                collider.OverlapPoint(worldTopLeft) ||
                collider.OverlapPoint(worldBottomRight) ||
                collider.OverlapPoint(worldTopRight))
            {
                return true;
            }

            Vector2 closestLocal = PathfinderObstacleScope.WorldToLevel(Level, collider.ClosestPoint(worldCenter));
            if (Mathf.Abs(closestLocal.x - center.x) <= halfCell && Mathf.Abs(closestLocal.y - center.y) <= halfCell)
            {
                return true;
            }

            if (xVelocity != 0 || yVelocity != 0)
            {
                Vector2 projectedCenter = center - new Vector2(xVelocity, yVelocity) * 2.5f;
                Vector2 projectedWorldCenter = PathfinderObstacleScope.LevelToWorld(Level, projectedCenter);
                closestLocal = PathfinderObstacleScope.WorldToLevel(Level, collider.ClosestPoint(projectedWorldCenter));
                return Mathf.Abs(closestLocal.x - projectedCenter.x) <= halfCell &&
                       Mathf.Abs(closestLocal.y - projectedCenter.y) <= halfCell;
            }

            return false;
        }

        private void FillObstaclePointIndexes(Obstacle obstacle, float xVelocity, float yVelocity, List<int> points, HashSet<int> pointSet)
        {
            points.Clear();
            pointSet.Clear();

            Collider2D collider = obstacle.ClearanceMappingCollider != null
                ? obstacle.ClearanceMappingCollider
                : obstacle.Collider;
            Bounds bounds = collider.bounds;
            float speedPaddingX = Mathf.Abs(xVelocity) * 2.5f;
            float speedPaddingY = Mathf.Abs(yVelocity) * 2.5f;
            bounds.Expand(new Vector3(speedPaddingX * 2f, speedPaddingY * 2f, 0));

            Vector2Int min = ConvertToMapCoordinates(
                PathfinderObstacleScope.WorldToLevel(Level, new Vector2(bounds.min.x, bounds.max.y)));
            Vector2Int max = ConvertToMapCoordinates(
                PathfinderObstacleScope.WorldToLevel(Level, new Vector2(bounds.max.x, bounds.min.y)));
            int startX = Mathf.Clamp(Mathf.Min(min.x, max.x), 0, _grid.MaxX);
            int endX = Mathf.Clamp(Mathf.Max(min.x, max.x), 0, _grid.MaxX);
            int startY = Mathf.Clamp(Mathf.Min(min.y, max.y), 0, _grid.MaxY);
            int endY = Mathf.Clamp(Mathf.Max(min.y, max.y), 0, _grid.MaxY);

            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    if (DoesColliderTouchNode(collider, x, y, xVelocity, yVelocity))
                    {
                        int index = ToIndex(x, y);
                        if (pointSet.Add(index))
                        {
                            points.Add(index);
                        }
                    }
                }
            }
        }

        public void UpdateMap(int threadIndex, Ship ship)
        {
            UpdateDynamicObstacleLayer();
            Array.Copy(_dynamicClearance, _threadClearance[threadIndex], _totalNodes);
        }

        private void UpdateDynamicObstacleLayer()
        {
            if (_staticObstacleLayerDirty)
            {
                RebuildStaticObstacleLayer();
            }

            if (!Level.ActivateCollisionAsteroids)
            {
                if (_dynamicLayerFrame != StaticDynamicLayerFrame)
                {
                    Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);
                    _dynamicLayerFrame = StaticDynamicLayerFrame;
                }
                return;
            }

            int frame = Level.Stage.FixedUpdates;
            if (_dynamicLayerFrame == frame)
            {
                return;
            }

            Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);

            for (int i = 0; i < Level.State.Obstacles.Count; i++)
            {
                Obstacle obstacle = Level.State.Obstacles[i];
                if (ShouldAvoidDynamicObstacle(obstacle))
                {
                    Vector2 velocity = Vector2.zero;
                    if (obstacle is CollisionAsteroid asteroid)
                    {
                        velocity = asteroid.Body.linearVelocity;
                    }

                    FillObstaclePointIndexes(obstacle, velocity.x, velocity.y, _obstaclePointIndexes, _obstaclePointIndexSet);
                    for (int pointIndex = 0; pointIndex < _obstaclePointIndexes.Count; pointIndex++)
                    {
                        _dynamicClearance[_obstaclePointIndexes[pointIndex]] = 0;
                    }
                }
            }

            CalculateClearance(_dynamicClearance, int.MaxValue);
            _dynamicLayerFrame = frame;
        }

        private bool ShouldBakeStaticObstacle(Obstacle obstacle)
        {
            if (obstacle == null ||
                obstacle.IsDead ||
                !obstacle.gameObject.activeInHierarchy ||
                obstacle.ObstacleType != ConfigData.ObstacleTypes.StaticObstacle)
            {
                return false;
            }

            Collider2D collider = obstacle.ClearanceMappingCollider != null
                ? obstacle.ClearanceMappingCollider
                : obstacle.Collider;
            return collider != null && collider.enabled;
        }

        private bool ShouldAvoidDynamicObstacle(Obstacle obstacle)
        {
            return obstacle != null &&
                !obstacle.IsDead &&
                obstacle.gameObject.activeInHierarchy &&
                obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid;
        }

        private void RebuildStaticObstacleLayer()
        {
            if (_baseClearance == null)
            {
                return;
            }

            for (int i = 0; i < _totalNodes; i++)
            {
                _baseClearance[i] = 1;
            }

            GameObject[] obstacles = PathfinderObstacleScope.GetActiveObstacleObjects(Level);
            for (int i = 0; i < obstacles.Length; i++)
            {
                Obstacle obstacle = obstacles[i].GetComponent<Obstacle>();
                if (!ShouldBakeStaticObstacle(obstacle))
                {
                    continue;
                }

                int[][] points = GetObstaclePoints(obstacle, 0, 0);
                if (obstacle.MapPointsIndex >= 0 && obstacle.MapPointsIndex < ObstaclePoints[0].Count)
                {
                    ObstaclePoints[0][obstacle.MapPointsIndex] = points;
                }

                for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
                {
                    int x = points[pointIndex][0];
                    int y = points[pointIndex][1];
                    if (x >= 0 && x < Width && y >= 0 && y < Height)
                    {
                        _baseClearance[ToIndex(x, y)] = 0;
                    }
                }
            }

            CalculateClearance(_baseClearance, int.MaxValue);
            _staticSignedClearance = BuildStaticSignedClearance(_baseClearance);
            SyncNodeClearanceFromBase();
            Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);
            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                Array.Copy(_baseClearance, _threadClearance[i], _totalNodes);
            }
            _staticObstacleLayerDirty = false;
            _dynamicLayerFrame = Level.ActivateCollisionAsteroids ? -1 : StaticDynamicLayerFrame;
        }
    }
}
