using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SW = System.Diagnostics;
using UnityEngine;
using System.Threading.Tasks;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace Assets.Scripts.Levels
{

    public class Pathfinder
    {

        public const int DIAGONAL_COST = 14;
        public const int HORIZONTAL_COST = 10;
        public const float TimeLimit = 5f;
        public int DebugLoops = 0;
        public int MaxLoopsPerFrame = 1000;

        private Grid _grid;
        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly
        /// </summary>
        public const int Scale = 4;
        public int Width, Height, HalfWidth, HalfHeight;
        public Level Level;
        public bool HasMovingObstacles;
        private int _totalNodes;
        private int[] _baseClearance;
        private int[] _dynamicClearance;
        private int[][] _threadClearance;
        private int[][] _costToHere;
        private int[][] _totalCost;
        private int[][] _heuristicCost;
        private int[][] _previousIndex;
        private int[][] _openStamp;
        private int[][] _closedStamp;
        private int[] _searchStamp;
        private int _nextRequestId;
        private int _dynamicLayerFrame = -1;
        private int _staticObstacleLayerSignature;
        private bool _staticObstacleLayerDirty;
        private readonly ConcurrentQueue<PathResult> _completedPaths = new ConcurrentQueue<PathResult>();
        private static readonly int[] NeighborX = new int[] { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] NeighborY = new int[] { -1, 0, 1, -1, 1, -1, 0, 1 };

        public Pathfinder(Level level)
        {
            Level = level;
            Setup();

        }

        public void Setup()
        {
            Width = (int)Math.Ceiling((double)Level.MapWidth / Scale);
            Height = (int)Math.Ceiling((double)Level.MapHeight / Scale);
            HalfWidth = (int)Math.Ceiling((double)Level.HalfMapWidth / Scale);
            HalfHeight = (int)Math.Ceiling((double)Level.HalfMapHeight / Scale);

            //Level.StartCoroutine(InitializeMap());
            InitializeMap();
        }
        // Utility methods
        public Vector2Int ConvertToMapCoordinates(Vector2 coords)
        {
            int x = (int)Math.Floor((Level.HalfMapWidth + coords.x) / Scale);
            int y = (int)Math.Floor((Level.HalfMapHeight - coords.y) / Scale);
            return new Vector2Int(Mathf.Clamp(x, 0, Width - 1), Mathf.Clamp(y, 0, Height - 1));
        }
        public Vector2 ConvertToLevelCoordinates(int x, int y)
        {
            return new Vector2((-Level.HalfMapWidth + (x * Scale)) + (Scale * 0.5f), (Level.HalfMapHeight - (y * Scale)) - (Scale * 0.5f));
        }
        public Vector2Int ConvertToLevelCoordinatesInt(int x, int y)
        {
            return Vector2Int.RoundToInt(ConvertToLevelCoordinates(x, y));
        }
        public void MarkObstacleLayerDirty()
        {
            _staticObstacleLayerDirty = true;
            _dynamicLayerFrame = -1;
        }
        private int ToIndex(int x, int y)
        {
            return (y * Width) + x;
        }
        private int ToX(int index)
        {
            return index % Width;
        }
        private int ToY(int index)
        {
            return index / Width;
        }
        public void Update()
        {
            ApplyCompletedPathResults();

            if (PathsWaiting.Count > 0)
            {
               

                for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads && PathsWaiting.Count > 0; threadIndex++)
                {
                    //Debug.Log($"Checking thread #{i} : {IsThreadActive[i]}, Pathswaiting: {PathsWaiting.Count}");
                    if (!IsThreadActive[threadIndex])
                    {
                        PathWaiting p = PathsWaiting.Dequeue();

                        // if this ship has already had a more recent path worked on, remove this path
                        while (ShipsToDequeue.Contains(p.Ship))
                        {
                            ShipsToDequeue.Remove(p.Ship);
                            ShipsQueued.Remove(p.Ship);
                            if (PathsWaiting.Count > 0)
                            {
                                p = PathsWaiting.Dequeue();
                            }
                            else
                            {
                                return;
                            }
                        }
                        if (p.Ship.PathfindingRequestId != p.RequestId)
                        {
                            ShipsQueued.Remove(p.Ship);
                            continue;
                        }

                        p.Ship.IsPathfinding = true;
                        IsThreadActive[threadIndex] = true;
                        Clearances[threadIndex] = p.Clearance;

                        UpdateMap(threadIndex, p.Ship);
                        StartNodes[threadIndex] = GridNodes[threadIndex][p.StartX][p.StartY];
                        EndNodes[threadIndex] = GridNodes[threadIndex][p.EndX][p.EndY];
                        Ships[threadIndex] = p.Ship;
                        RequestIds[threadIndex] = p.RequestId;

                        //Debug.Log($"Queued Started BT #{i}");

                        //Debug.Log($"Queued running #{i} for {p.Ship.Name}");

                        BTFindPath(threadIndex);

                        //Debug.Log($"Called BTFindPath({i}) #{i} for {p.Ship.Name}");

                        //Debug.Log($"Thread #{threadIndex}:{Ships[threadIndex].Name} Queued Started after waiting {(Time.realtimeSinceStartup - p.StartTime) * 1000}ms on the queue");

                        //ThreadsStarted++;
                        //PathsWaitingToRemove.Add(p);
                        ShipsQueued.Remove(p.Ship);
                        //break;

                    }
                    else
                    {
                        if (Totals[threadIndex] != null && Totals[threadIndex].ElapsedMilliseconds > 1000)
                        {
                            Debug.Log($"Thread #{threadIndex}:{Ships[threadIndex].Name} has been running for {Totals[threadIndex].ElapsedMilliseconds}ms");
                        }
                    }
                }

                //Debug.Log($"There are NOW {PathsWaiting.Count} paths waiting at {Time.realtimeSinceStartup}");
            }

            //for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads; threadIndex++)
            //{
            //    if (Totals[threadIndex] != null && IsThreadActive[threadIndex] && Totals[threadIndex].ElapsedMilliseconds > 1000)
            //    {
            //        Debug.Log($"Thread #{threadIndex}:{Ships[threadIndex].Name} has been running for {Totals[threadIndex].ElapsedMilliseconds}ms");
            //    }else if (Totals[threadIndex] == null)
            //    {
            //        Debug.Log($"Totals[{threadIndex}] is null");
            //    }else if (!IsThreadActive[threadIndex])
            //    {
            //        Debug.Log($"Thread #{threadIndex} is empty");
            //    }
            //    else
            //    {
            //        Debug.Log($"Not enough time has elapsed for #{threadIndex}: {Totals[threadIndex].ElapsedMilliseconds}ms");
            //    }
            //}
               

        }

        //int totalLoopCount = 0;
        int minY, minX, maxY, maxX, boundsX, boundsY, x, y, yMovement, nextX = 0;
        bool hasHitObstacle;
        MapNode currentNode, loopNode, previousNode;
        //int loopsSaved = 0;
        public void CalculateClearance(MapNode[][] nodes, int startX, int endX, int startY, int endY, int maxClearance, bool isSubSection)
        {
            int clearanceCap = maxClearance == int.MaxValue ? Mathf.Max(_grid.Width, _grid.Height) : maxClearance;

            for (y = startY; y < endY; y++)
            {
                for (x = startX; x < endX; x++)
                {
                    currentNode = nodes[x][y];
                    if (currentNode.Clearance > 0)
                    {
                        currentNode.Clearance = Mathf.Min(Mathf.Min(clearanceCap, x + 1), Mathf.Min(y + 1, Mathf.Min(_grid.Width - x, _grid.Height - y)));
                    }
                }
            }

            for (y = startY; y < endY; y++)
            {
                for (x = startX; x < endX; x++)
                {
                    currentNode = nodes[x][y];
                    if (currentNode.Clearance == 0)
                    {
                        continue;
                    }

                    int clearance = currentNode.Clearance;
                    if (x > 0)
                    {
                        clearance = Mathf.Min(clearance, nodes[x - 1][y].Clearance + 1);
                    }
                    if (y > 0)
                    {
                        clearance = Mathf.Min(clearance, nodes[x][y - 1].Clearance + 1);
                    }
                    if (x > 0 && y > 0)
                    {
                        clearance = Mathf.Min(clearance, nodes[x - 1][y - 1].Clearance + 1);
                    }
                    if (x < _grid.MaxX && y > 0)
                    {
                        clearance = Mathf.Min(clearance, nodes[x + 1][y - 1].Clearance + 1);
                    }

                    currentNode.Clearance = clearance;
                }
            }

            for (y = endY - 1; y >= startY; y--)
            {
                for (x = endX - 1; x >= startX; x--)
                {
                    currentNode = nodes[x][y];
                    if (currentNode.Clearance == 0)
                    {
                        continue;
                    }

                    int clearance = currentNode.Clearance;
                    if (x < _grid.MaxX)
                    {
                        clearance = Mathf.Min(clearance, nodes[x + 1][y].Clearance + 1);
                    }
                    if (y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, nodes[x][y + 1].Clearance + 1);
                    }
                    if (x < _grid.MaxX && y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, nodes[x + 1][y + 1].Clearance + 1);
                    }
                    if (x > 0 && y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, nodes[x - 1][y + 1].Clearance + 1);
                    }

                    currentNode.Clearance = clearance;
                    if (!isSubSection)
                    {
                        currentNode.OriginalClearance = currentNode.Clearance;
                    }
                }
            }
        }

        private void ApplyCompletedPathResults()
        {
            while (_completedPaths.TryDequeue(out PathResult result))
            {
                IsThreadActive[result.ThreadIndex] = false;
                if (result.Ship == null || result.Ship.PathfindingRequestId != result.RequestId)
                {
                    continue;
                }

                result.Ship.PathfindingValue = result.Path;
                result.Ship.PathfindingThreadComplete = true;
            }
        }
        private void CalculateClearance(int[] clearanceMap, int maxClearance)
        {
            int clearanceCap = maxClearance == int.MaxValue ? Mathf.Max(Width, Height) : maxClearance;

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = ToIndex(x, y);
                    if (clearanceMap[index] > 0)
                    {
                        clearanceMap[index] = Mathf.Min(Mathf.Min(clearanceCap, x + 1), Mathf.Min(y + 1, Mathf.Min(Width - x, Height - y)));
                    }
                }
            }

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = ToIndex(x, y);
                    if (clearanceMap[index] == 0)
                    {
                        continue;
                    }

                    int clearance = clearanceMap[index];
                    if (x > 0)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x - 1, y)] + 1);
                    }
                    if (y > 0)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x, y - 1)] + 1);
                    }
                    if (x > 0 && y > 0)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x - 1, y - 1)] + 1);
                    }
                    if (x < _grid.MaxX && y > 0)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x + 1, y - 1)] + 1);
                    }

                    clearanceMap[index] = clearance;
                }
            }

            for (int y = Height - 1; y >= 0; y--)
            {
                for (int x = Width - 1; x >= 0; x--)
                {
                    int index = ToIndex(x, y);
                    if (clearanceMap[index] == 0)
                    {
                        continue;
                    }

                    int clearance = clearanceMap[index];
                    if (x < _grid.MaxX)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x + 1, y)] + 1);
                    }
                    if (y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x, y + 1)] + 1);
                    }
                    if (x < _grid.MaxX && y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x + 1, y + 1)] + 1);
                    }
                    if (x > 0 && y < _grid.MaxY)
                    {
                        clearance = Mathf.Min(clearance, clearanceMap[ToIndex(x - 1, y + 1)] + 1);
                    }

                    clearanceMap[index] = clearance;
                }
            }
        }
        public void InitializeMap()
        {
            float start = Time.realtimeSinceStartup;
            //Debug.Log($"Loading pathfinder map at {Scale}x");
            // initialize everything as open space

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



            GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");
            //Debug.Log($"There are {obstacles.Length} obstacles to map: {Utilities.ListToString(obstacles.ToList())}");

            // initialize obstacle points lists
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
                ObstaclePoints[0][obstacle.MapPointsIndex] = ShouldBakeStaticObstacle(obstacle) ? GetObstaclePoints(obstacle, 0, 0) : new int[][] { };

                //Debug.Log($"The first point on {obstacle.Name} is ({ObstaclePoints[obstacle.Id][0][0]}, {ObstaclePoints[obstacle.Id][0][1]}) on the map");

                foreach (int[] point in ObstaclePoints[0][obstacle.MapPointsIndex])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                        _grid.Nodes[point[0]][point[1]].Clearance = 0; // set to unwalkable space
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
            Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);

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

            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {

                // initalize list of previous asteroids
                PreviousAsteroids[i] = new List<int>();

                // The hot path derives neighbors by index now; MapNode neighbors are left lazy for debugging only.
            }

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            //Debug.Log($"Initialized map in {end} ms");


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
            //Debug.Log($"Adding {obstacle.Name} to Map with index: {(ObstaclePoints[0].Count - 1)}");
            return ObstaclePoints[0].Count - 1;
        }

        /// <summary>
        /// Gets all the points on an obstacle in the game and converts them to an array of points in the pathfinding map
        /// </summary>
        /// <param name="obstacle"></param>
        public int[][] GetObstaclePoints(Obstacle obstacle, float xVelocity, float yVelocity)
        {
            Collider2D collider = obstacle.ClearanceMappingCollider;

            if (collider == null)
            {
                //Debug.Log($"{obstacle.Name} does not have a proximity collider");
                collider = obstacle.Collider;
            }

            Bounds bounds = collider.bounds;
            float speedPaddingX = Mathf.Abs(xVelocity) * 2.5f;
            float speedPaddingY = Mathf.Abs(yVelocity) * 2.5f;
            bounds.Expand(new Vector3(speedPaddingX * 2f, speedPaddingY * 2f, 0));

            Vector2Int min = ConvertToMapCoordinates(new Vector2(bounds.min.x, bounds.max.y));
            Vector2Int max = ConvertToMapCoordinates(new Vector2(bounds.max.x, bounds.min.y));
            int startX = Mathf.Clamp(Mathf.Min(min.x, max.x) - 1, 0, _grid.MaxX);
            int endX = Mathf.Clamp(Mathf.Max(min.x, max.x) + 1, 0, _grid.MaxX);
            int startY = Mathf.Clamp(Mathf.Min(min.y, max.y) - 1, 0, _grid.MaxY);
            int endY = Mathf.Clamp(Mathf.Max(min.y, max.y) + 1, 0, _grid.MaxY);

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
            if (collider.OverlapPoint(center) ||
                collider.OverlapPoint(new Vector2(center.x - halfCell, center.y - halfCell)) ||
                collider.OverlapPoint(new Vector2(center.x - halfCell, center.y + halfCell)) ||
                collider.OverlapPoint(new Vector2(center.x + halfCell, center.y - halfCell)) ||
                collider.OverlapPoint(new Vector2(center.x + halfCell, center.y + halfCell)))
            {
                return true;
            }

            Vector2 closest = collider.ClosestPoint(center);
            if (Mathf.Abs(closest.x - center.x) <= halfCell && Mathf.Abs(closest.y - center.y) <= halfCell)
            {
                return true;
            }

            if (xVelocity != 0 || yVelocity != 0)
            {
                Vector2 projectedCenter = center - new Vector2(xVelocity, yVelocity) * 2.5f;
                closest = collider.ClosestPoint(projectedCenter);
                return Mathf.Abs(closest.x - projectedCenter.x) <= halfCell && Mathf.Abs(closest.y - projectedCenter.y) <= halfCell;
            }

            return false;
        }
        public void UpdateMap(int threadIndex, Ship ship)
        {
            UpdateDynamicObstacleLayer();
            Array.Copy(_dynamicClearance, _threadClearance[threadIndex], _totalNodes);
        }

        private void UpdateDynamicObstacleLayer()
        {
            int staticSignature = CalculateStaticObstacleLayerSignature();
            if (_staticObstacleLayerDirty || staticSignature != _staticObstacleLayerSignature)
            {
                RebuildStaticObstacleLayer(staticSignature);
            }

            if (!Level.ActivateCollisionAsteroids)
            {
                if (_dynamicLayerFrame != Level.Stage.FixedUpdates)
                {
                    Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);
                    _dynamicLayerFrame = Level.Stage.FixedUpdates;
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
                    else if (obstacle is AsteroidPiece asteroidPiece)
                    {
                        velocity = asteroidPiece.Body.linearVelocity;
                    }

                    int[][] points = GetObstaclePoints(obstacle, velocity.x, velocity.y);
                    for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
                    {
                        int x = points[pointIndex][0];
                        int y = points[pointIndex][1];
                        if (x >= 0 && x < Width && y >= 0 && y < Height)
                        {
                            _dynamicClearance[ToIndex(x, y)] = 0;
                        }
                    }
                }
            }

            CalculateClearance(_dynamicClearance, int.MaxValue);
            _dynamicLayerFrame = frame;
        }

        private int CalculateStaticObstacleLayerSignature()
        {
            unchecked
            {
                int signature = 17;
                GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");
                for (int i = 0; i < obstacles.Length; i++)
                {
                    Obstacle obstacle = obstacles[i].GetComponent<Obstacle>();
                    if (!ShouldBakeStaticObstacle(obstacle))
                    {
                        continue;
                    }

                    signature = (signature * 31) + obstacle.GetInstanceID();
                    signature = (signature * 31) + obstacle.transform.position.GetHashCode();
                    signature = (signature * 31) + obstacle.transform.lossyScale.GetHashCode();
                }
                return signature;
            }
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

            Collider2D collider = obstacle.ClearanceMappingCollider != null ? obstacle.ClearanceMappingCollider : obstacle.Collider;
            return collider != null && collider.enabled;
        }

        private bool ShouldAvoidDynamicObstacle(Obstacle obstacle)
        {
            return obstacle != null &&
                !obstacle.IsDead &&
                obstacle.gameObject.activeInHierarchy &&
                (obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid ||
                 obstacle.ObstacleType == ConfigData.ObstacleTypes.AsteroidPiece);
        }

        private void RebuildStaticObstacleLayer(int staticSignature)
        {
            if (_baseClearance == null)
            {
                return;
            }

            for (int i = 0; i < _totalNodes; i++)
            {
                _baseClearance[i] = 1;
            }

            GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");
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
            SyncNodeClearanceFromBase();
            Array.Copy(_baseClearance, _dynamicClearance, _totalNodes);
            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                Array.Copy(_baseClearance, _threadClearance[i], _totalNodes);
            }
            _staticObstacleLayerDirty = false;
            _staticObstacleLayerSignature = staticSignature;
            _dynamicLayerFrame = -1;
        }

        private void SyncNodeClearanceFromBase()
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int clearance = _baseClearance[ToIndex(x, y)];
                    _grid.Nodes[x][y].Clearance = clearance;
                    _grid.Nodes[x][y].OriginalClearance = clearance;
                    for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads; threadIndex++)
                    {
                        if (GridNodes[threadIndex] != null)
                        {
                            GridNodes[threadIndex][x][y].Clearance = clearance;
                            GridNodes[threadIndex][x][y].OriginalClearance = clearance;
                        }
                    }
                }
            }
        }
        public MapNode FindNearestWalkablePoint(MapNode startNode, MapNode endNode, int minimumClearance, int threadIndex)
        {
            if (startNode.Clearance >= minimumClearance)
            {
                return startNode;
            }

            MapNode bestNode = MapNode.NullNode;
            int bestCost = int.MaxValue;
            int maxRadius = Mathf.Max(_grid.Width, _grid.Height);

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minX = Mathf.Max(0, startNode.x - radius);
                int maxX = Mathf.Min(_grid.MaxX, startNode.x + radius);
                int minY = Mathf.Max(0, startNode.y - radius);
                int maxY = Mathf.Min(_grid.MaxY, startNode.y + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    CheckNearestWalkableCandidate(GridNodes[threadIndex][x][minY], endNode, minimumClearance, ref bestNode, ref bestCost);
                    CheckNearestWalkableCandidate(GridNodes[threadIndex][x][maxY], endNode, minimumClearance, ref bestNode, ref bestCost);
                }

                for (int y = minY + 1; y < maxY; y++)
                {
                    CheckNearestWalkableCandidate(GridNodes[threadIndex][minX][y], endNode, minimumClearance, ref bestNode, ref bestCost);
                    CheckNearestWalkableCandidate(GridNodes[threadIndex][maxX][y], endNode, minimumClearance, ref bestNode, ref bestCost);
                }

                if (bestNode != MapNode.NullNode)
                {
                    return bestNode;
                }
            }

            return MapNode.NullNode;

        }

        private void CheckNearestWalkableCandidate(MapNode node, MapNode endNode, int minimumClearance, ref MapNode bestNode, ref int bestCost)
        {
            if (node.Clearance < minimumClearance)
            {
                return;
            }

            int cost = MapNode.CalculateDistance(node, endNode);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestNode = node;
            }
        }
        private void MakeDestinationList(MapNode BTEndNode, Path BTPath)
        {
            List<Vector2> BTDestinationList = new List<Vector2> { BTEndNode.Vector };
            
            MapNode BTCurrentNode = BTEndNode;
            Vector2Int previousSlope = Vector2Int.zero;
            Vector2Int slope = Vector2Int.one;

            while (BTCurrentNode.PreviousNode != MapNode.NullNode)
            {
                //Debug.Log(currentNode.PreviousNode.Id);

                slope = new Vector2Int(BTCurrentNode.x - BTCurrentNode.PreviousNode.x, BTCurrentNode.y - BTCurrentNode.PreviousNode.y);
                //Debug.Log($"The slope for the point is {slope}, is is the same as the previous slope? {previousSlope == slope}");

                if (previousSlope != slope)
                {
                    BTDestinationList.Add(BTCurrentNode.PreviousNode.Vector);
                    BTCurrentNode.PreviousNode.IsPartOfPath = true;
                }
                previousSlope = slope;
                BTCurrentNode = BTCurrentNode.PreviousNode;

            }

            BTDestinationList.Reverse();
            //path.SetPoints(destinationList);
            //Debug.Log($"There are {BTDestinationList.Count} points in the path");
            BTPath.Points = BTDestinationList;
        }
        private MapNode GetCheapestNode(List<MapNode> list, MapNode previousNode)
        {
            MapNode cheapest = list[0];
            for (int cheapestIterator = 1; cheapestIterator < list.Count; cheapestIterator++)
            {
                if (cheapest.TotalCost - previousNode.TotalCost <= 1)
                {
                    return cheapest;
                }
                if (list[cheapestIterator].TotalCost < cheapest.TotalCost)
                {
                    cheapest = list[cheapestIterator];
                }
            }
            return cheapest;
        }

        private bool IsDiagonalMoveBlocked(MapNode currentNode, MapNode neighbor, int clearance, int threadIndex)
        {
            int xDistance = Mathf.Abs(currentNode.x - neighbor.x);
            int yDistance = Mathf.Abs(currentNode.y - neighbor.y);
            if (xDistance != 1 || yDistance != 1)
            {
                return false;
            }

            return GridNodes[threadIndex][currentNode.x][neighbor.y].Clearance < clearance ||
                   GridNodes[threadIndex][neighbor.x][currentNode.y].Clearance < clearance;
        }

        private bool IsDiagonalMoveBlocked(int currentX, int currentY, int neighborX, int neighborY, int clearance, int[] clearanceMap)
        {
            if (Mathf.Abs(currentX - neighborX) != 1 || Mathf.Abs(currentY - neighborY) != 1)
            {
                return false;
            }

            return clearanceMap[ToIndex(currentX, neighborY)] < clearance ||
                   clearanceMap[ToIndex(neighborX, currentY)] < clearance;
        }

        private int CalculateDistance(int a, int b)
        {
            int xDistance = Mathf.Abs(ToX(a) - ToX(b));
            int yDistance = Mathf.Abs(ToY(a) - ToY(b));
            return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * Mathf.Abs(xDistance - yDistance);
        }

        private class MinHeap
        {
            private readonly List<MapNode> _nodes = new List<MapNode>();

            public int Count => _nodes.Count;

            public void Push(MapNode node)
            {
                _nodes.Add(node);
                int index = _nodes.Count - 1;

                while (index > 0)
                {
                    int parentIndex = (index - 1) / 2;
                    if (IsHigherPriority(_nodes[parentIndex], node))
                    {
                        break;
                    }

                    _nodes[index] = _nodes[parentIndex];
                    index = parentIndex;
                }

                _nodes[index] = node;
            }

            public MapNode Pop()
            {
                MapNode result = _nodes[0];
                MapNode last = _nodes[_nodes.Count - 1];
                _nodes.RemoveAt(_nodes.Count - 1);

                if (_nodes.Count == 0)
                {
                    return result;
                }

                int index = 0;
                while (true)
                {
                    int leftIndex = (index * 2) + 1;
                    if (leftIndex >= _nodes.Count)
                    {
                        break;
                    }

                    int rightIndex = leftIndex + 1;
                    int childIndex = rightIndex < _nodes.Count && IsHigherPriority(_nodes[rightIndex], _nodes[leftIndex]) ? rightIndex : leftIndex;

                    if (IsHigherPriority(last, _nodes[childIndex]))
                    {
                        break;
                    }

                    _nodes[index] = _nodes[childIndex];
                    index = childIndex;
                }

                _nodes[index] = last;
                return result;
            }

            private static bool IsHigherPriority(MapNode a, MapNode b)
            {
                if (a.TotalCost != b.TotalCost)
                {
                    return a.TotalCost < b.TotalCost;
                }

                if (a.HueristicCost != b.HueristicCost)
                {
                    return a.HueristicCost < b.HueristicCost;
                }

                return a.Id < b.Id;
            }
        }

        private class IntMinHeap
        {
            private readonly List<int> _nodes = new List<int>();
            private readonly int[] _totalCost;
            private readonly int[] _heuristicCost;

            public int Count => _nodes.Count;

            public IntMinHeap(int[] totalCost, int[] heuristicCost)
            {
                _totalCost = totalCost;
                _heuristicCost = heuristicCost;
            }

            public void Push(int node)
            {
                _nodes.Add(node);
                int index = _nodes.Count - 1;

                while (index > 0)
                {
                    int parentIndex = (index - 1) / 2;
                    if (IsHigherPriority(_nodes[parentIndex], node))
                    {
                        break;
                    }

                    _nodes[index] = _nodes[parentIndex];
                    index = parentIndex;
                }

                _nodes[index] = node;
            }

            public int Pop()
            {
                int result = _nodes[0];
                int last = _nodes[_nodes.Count - 1];
                _nodes.RemoveAt(_nodes.Count - 1);

                if (_nodes.Count == 0)
                {
                    return result;
                }

                int index = 0;
                while (true)
                {
                    int leftIndex = (index * 2) + 1;
                    if (leftIndex >= _nodes.Count)
                    {
                        break;
                    }

                    int rightIndex = leftIndex + 1;
                    int childIndex = rightIndex < _nodes.Count && IsHigherPriority(_nodes[rightIndex], _nodes[leftIndex]) ? rightIndex : leftIndex;

                    if (IsHigherPriority(last, _nodes[childIndex]))
                    {
                        break;
                    }

                    _nodes[index] = _nodes[childIndex];
                    index = childIndex;
                }

                _nodes[index] = last;
                return result;
            }

            private bool IsHigherPriority(int a, int b)
            {
                if (_totalCost[a] != _totalCost[b])
                {
                    return _totalCost[a] < _totalCost[b];
                }

                if (_heuristicCost[a] != _heuristicCost[b])
                {
                    return _heuristicCost[a] < _heuristicCost[b];
                }

                return a < b;
            }
        }

        public Queue<PathWaiting> PathsWaiting = new Queue<PathWaiting>();
        public List<PathWaiting> PathsWaitingToRemove = new List<PathWaiting>();
        /// <summary>
        /// Keeps track of ships that have paths waiting on the queue
        /// </summary>
        public HashSet<Ship> ShipsQueued = new HashSet<Ship>();
        /// <summary>
        /// Keeps track of ships that did not go onto the queue but were worked on immediately. If those same ships are on the queue the path shouldn't be worked on
        /// </summary>
        public HashSet<Ship> ShipsToDequeue = new HashSet<Ship>();
        public bool[] IsThreadActive = new bool[ConfigData.MaxThreads];
        public List<int>[] PreviousAsteroids = new List<int>[ConfigData.MaxThreads];
        /// <summary>
        /// A list of arrays of obstacle points. Each array of points belongs to an obstacle and each point (a two int array) is an x index and a y index on the Map array
        /// </summary>
        public List<int[][]>[] ObstaclePoints = new List<int[][]>[ConfigData.MaxThreads];


        public SW.Stopwatch[] Totals = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] NeighborLoops = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] GetNodes = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] UpdateMapTime = new SW.Stopwatch[ConfigData.MaxThreads];
        public MapNode[] StartNodes = new MapNode[ConfigData.MaxThreads];
        public MapNode[] EndNodes = new MapNode[ConfigData.MaxThreads];
        public int[] Clearances = new int[ConfigData.MaxThreads];
        public int[] RequestIds = new int[ConfigData.MaxThreads];
        public Ship[] Ships = new Ship[ConfigData.MaxThreads];
        public MapNode[][][] GridNodes = new MapNode[ConfigData.MaxThreads][][];

        public class PathWaiting
        {
            public Ship Ship;
            public int Clearance, StartX, StartY, EndX, EndY, RequestId;
            public float StartTime = Time.realtimeSinceStartup;

            public PathWaiting(Ship ship, int startX, int startY, int endX, int endY, int clearance, int requestId)
            {
                Ship = ship;
                StartX = startX;
                StartY = startY; 
                EndX = endX; 
                EndY = endY;
                Clearance = clearance;
                RequestId = requestId;
            }
        }
        private class PathResult
        {
            public Ship Ship;
            public int RequestId, ThreadIndex;
            public Path Path;

            public PathResult(Ship ship, int requestId, int threadIndex, Path path)
            {
                Ship = ship;
                RequestId = requestId;
                ThreadIndex = threadIndex;
                Path = path;
            }
        }
        private Path RunPathSearch(int threadIndex)
        {
            int clearance = GetEffectivePathClearance(Clearances[threadIndex]);
            int startIndex = ToIndex(StartNodes[threadIndex].x, StartNodes[threadIndex].y);
            int endIndex = ToIndex(EndNodes[threadIndex].x, EndNodes[threadIndex].y);
            int[] clearanceMap = _threadClearance[threadIndex];

            if (clearanceMap[endIndex] < clearance)
            {
                endIndex = FindNearestWalkableIndex(endIndex, startIndex, clearance, threadIndex);
            }
            if (endIndex < 0)
            {
                return null;
            }

            if (clearanceMap[startIndex] < clearance)
            {
                startIndex = FindNearestWalkableIndex(startIndex, endIndex, clearance, threadIndex);
            }
            if (startIndex < 0)
            {
                return null;
            }

            int searchStamp = ++_searchStamp[threadIndex];
            if (searchStamp == int.MaxValue)
            {
                Array.Clear(_openStamp[threadIndex], 0, _totalNodes);
                Array.Clear(_closedStamp[threadIndex], 0, _totalNodes);
                searchStamp = _searchStamp[threadIndex] = 1;
            }

            int[] costToHere = _costToHere[threadIndex];
            int[] totalCost = _totalCost[threadIndex];
            int[] heuristicCost = _heuristicCost[threadIndex];
            int[] previousIndex = _previousIndex[threadIndex];
            int[] openStamp = _openStamp[threadIndex];
            int[] closedStamp = _closedStamp[threadIndex];

            IntMinHeap open = new IntMinHeap(totalCost, heuristicCost);
            costToHere[startIndex] = 0;
            heuristicCost[startIndex] = CalculateDistance(startIndex, endIndex);
            totalCost[startIndex] = heuristicCost[startIndex];
            previousIndex[startIndex] = -1;
            openStamp[startIndex] = searchStamp;
            open.Push(startIndex);

            while (open.Count > 0 && Totals[threadIndex].Elapsed.TotalSeconds < TimeLimit)
            {
                int currentIndex = open.Pop();
                if (closedStamp[currentIndex] == searchStamp)
                {
                    continue;
                }

                if (currentIndex == endIndex)
                {
                    return MakeDestinationList(startIndex, endIndex, previousIndex, clearanceMap, clearance);
                }

                closedStamp[currentIndex] = searchStamp;
                int currentX = ToX(currentIndex);
                int currentY = ToY(currentIndex);

                for (int i = 0; i < NeighborX.Length; i++)
                {
                    int neighborX = currentX + NeighborX[i];
                    int neighborY = currentY + NeighborY[i];
                    if (neighborX < 0 || neighborY < 0 || neighborX >= Width || neighborY >= Height)
                    {
                        continue;
                    }

                    int neighborIndex = ToIndex(neighborX, neighborY);
                    if (closedStamp[neighborIndex] == searchStamp ||
                        clearanceMap[neighborIndex] < clearance ||
                        IsDiagonalMoveBlocked(currentX, currentY, neighborX, neighborY, clearance, clearanceMap))
                    {
                        continue;
                    }

                    int newCostToHere = costToHere[currentIndex] + CalculateDistance(currentIndex, neighborIndex);
                    if (openStamp[neighborIndex] != searchStamp || newCostToHere < costToHere[neighborIndex])
                    {
                        costToHere[neighborIndex] = newCostToHere;
                        heuristicCost[neighborIndex] = CalculateDistance(neighborIndex, endIndex);
                        totalCost[neighborIndex] = newCostToHere + heuristicCost[neighborIndex];
                        previousIndex[neighborIndex] = currentIndex;
                        openStamp[neighborIndex] = searchStamp;
                        open.Push(neighborIndex);
                    }
                }
            }

            return null;
        }

        private int GetEffectivePathClearance(int shipClearance)
        {
            return Mathf.Max(1, shipClearance > ConfigData.MinimumClearance ? shipClearance - 1 : shipClearance - 2);
            // Account for collision detection using 1.2x multiplier. Reduce by 1-2 but apply the multiplier
            // to ensure waypoints account for the larger collision box used during movement.
            int adjustedClearance = Mathf.CeilToInt(shipClearance * 1.2f);
            return Mathf.Max(1, adjustedClearance > ConfigData.MinimumClearance ? adjustedClearance - 1 : adjustedClearance - 2);
        }

        private Path MakeDestinationList(int startIndex, int endIndex, int[] previousIndex, int[] clearanceMap, int clearance)
        {
            Path path = new Path(ToX(startIndex), ToY(startIndex), ToX(endIndex), ToY(endIndex));
            List<int> indexes = new List<int> { endIndex };
            int currentIndex = endIndex;

            while (currentIndex != startIndex && previousIndex[currentIndex] >= 0)
            {
                int previous = previousIndex[currentIndex];
                indexes.Add(previous);
                currentIndex = previous;
            }

            indexes.Reverse();
            if (indexes.Count > 1)
            {
                indexes.RemoveAt(0);
            }

            indexes = SmoothPathIndexes(indexes, clearanceMap, clearance);

            List<Vector2> points = new List<Vector2>();
            for (int i = 0; i < indexes.Count; i++)
            {
                points.Add(ConvertToLevelCoordinates(ToX(indexes[i]), ToY(indexes[i])));
            }

            path.Points = points;
            return path;
        }

        private List<int> SmoothPathIndexes(List<int> indexes, int[] clearanceMap, int clearance)
        {
            if (indexes.Count <= 2)
            {
                return indexes;
            }

            List<int> smoothed = new List<int>();
            int current = 0;
            smoothed.Add(indexes[current]);

            while (current < indexes.Count - 1)
            {
                int next = indexes.Count - 1;
                while (next > current + 1 && !HasClearGridLine(indexes[current], indexes[next], clearanceMap, clearance))
                {
                    next--;
                }

                smoothed.Add(indexes[next]);
                current = next;
            }

            return smoothed;
        }

        private bool HasClearGridLine(int startIndex, int endIndex, int[] clearanceMap, int clearance)
        {
            int x0 = ToX(startIndex);
            int y0 = ToY(startIndex);
            int x1 = ToX(endIndex);
            int y1 = ToY(endIndex);
            int dx = Mathf.Abs(x1 - x0);
            int dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int error = dx - dy;

            while (true)
            {
                int index = ToIndex(x0, y0);
                if (clearanceMap[index] < clearance)
                {
                    return false;
                }

                if (x0 == x1 && y0 == y1)
                {
                    return true;
                }

                int e2 = error * 2;
                int nextX = x0;
                int nextY = y0;
                if (e2 > -dy)
                {
                    error -= dy;
                    nextX += sx;
                }
                if (e2 < dx)
                {
                    error += dx;
                    nextY += sy;
                }

                if (nextX != x0 && nextY != y0 && IsDiagonalMoveBlocked(x0, y0, nextX, nextY, clearance, clearanceMap))
                {
                    return false;
                }

                x0 = nextX;
                y0 = nextY;
            }
        }

        private int FindNearestWalkableIndex(int startIndex, int endIndex, int minimumClearance, int threadIndex)
        {
            int[] clearanceMap = _threadClearance[threadIndex];
            if (clearanceMap[startIndex] >= minimumClearance)
            {
                return startIndex;
            }

            int startX = ToX(startIndex);
            int startY = ToY(startIndex);
            int bestIndex = -1;
            int bestCost = int.MaxValue;
            int maxRadius = Mathf.Max(Width, Height);

            for (int radius = 1; radius <= maxRadius; radius++)
            {
                int minX = Mathf.Max(0, startX - radius);
                int maxX = Mathf.Min(_grid.MaxX, startX + radius);
                int minY = Mathf.Max(0, startY - radius);
                int maxY = Mathf.Min(_grid.MaxY, startY + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    CheckNearestWalkableIndex(ToIndex(x, minY), endIndex, minimumClearance, clearanceMap, ref bestIndex, ref bestCost);
                    CheckNearestWalkableIndex(ToIndex(x, maxY), endIndex, minimumClearance, clearanceMap, ref bestIndex, ref bestCost);
                }

                for (int y = minY + 1; y < maxY; y++)
                {
                    CheckNearestWalkableIndex(ToIndex(minX, y), endIndex, minimumClearance, clearanceMap, ref bestIndex, ref bestCost);
                    CheckNearestWalkableIndex(ToIndex(maxX, y), endIndex, minimumClearance, clearanceMap, ref bestIndex, ref bestCost);
                }

                if (bestIndex >= 0)
                {
                    return bestIndex;
                }
            }

            return -1;
        }

        private void CheckNearestWalkableIndex(int index, int endIndex, int minimumClearance, int[] clearanceMap, ref int bestIndex, ref int bestCost)
        {
            if (clearanceMap[index] < minimumClearance)
            {
                return;
            }

            int cost = CalculateDistance(index, endIndex);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestIndex = index;
            }
        }
        public async Task BTFindPath(int threadIndex)
        {
            await Task.Run(() =>
            {
                Totals[threadIndex] = SW.Stopwatch.StartNew();
                Path path = RunPathSearch(threadIndex);
                _completedPaths.Enqueue(new PathResult(Ships[threadIndex], RequestIds[threadIndex], threadIndex, path));
            }).ContinueWith((task) =>
            {
                //Debug.Log($"Has continued task for #{threadIndex}:{Ships[threadIndex].Name}");
                if (task.IsFaulted)
                {
                    AggregateException aggregateException = task.Exception;
                    foreach (Exception exception in aggregateException.InnerExceptions)
                    {
                        Debug.LogException(exception);
                    }
                    _completedPaths.Enqueue(new PathResult(Ships[threadIndex], RequestIds[threadIndex], threadIndex, null));
                }
                else
                {
                    //Debug.Log($"No exceptions for #{threadIndex}:{Ships[threadIndex].Name}");
                }

                //Debug.Log($"Has continued to end of task for #{threadIndex}:{Ships[threadIndex].Name}");
            });


            //Totals[threadIndex].Stop();
            //_grid.DebugGridAsImage(StartNodes[threadIndex].Index, EndNodes[threadIndex].Index, GridNodes[threadIndex], 4, Ships[threadIndex]);
            //Ships[threadIndex].PathfindingThreadComplete = true;
            //IsThreadActive[threadIndex] = false; //[alert] must be uncommented when not testing
            //Debug.Log($"Finished background finding path and destination list and thread for #{threadIndex}:{Ships[threadIndex].Name}.  Total: {(Totals[threadIndex].Elapsed.TotalMilliseconds)}ms");

        }

        public void FindPath(Ship ship, int startX, int startY, int endX, int endY, int maximumClearance)
        {
            //Debug.Log($"Starting pathfinding for {ship.Name}");
            bool startedTask = false;
            int requestId = ++_nextRequestId;
            ship.PathfindingRequestId = requestId;
            startX = Mathf.Clamp(startX, 0, _grid.MaxX);
            startY = Mathf.Clamp(startY, 0, _grid.MaxY);
            endX = Mathf.Clamp(endX, 0, _grid.MaxX);
            endY = Mathf.Clamp(endY, 0, _grid.MaxY);
            //bool foundShipsThread = false;

            //for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads; threadIndex++)
            //{
            //    if (Ships[threadIndex] == ship && IsThreadActive[threadIndex])
            //    {
            //        KillThread[threadIndex] = true;
            //        foundShipsThread = true;
            //        ship.MandatoryThread = threadIndex;
            //    }
            //}

            for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads; threadIndex++)
            {
                if (!IsThreadActive[threadIndex])
                {
                    ship.IsPathfinding = true;
                    IsThreadActive[threadIndex] = true;
                    Clearances[threadIndex] = maximumClearance;
                    RequestIds[threadIndex] = requestId;
                    UpdateMap(threadIndex, ship);
                    //Debug.Log($"Pre starting Finding path for #{i} from {startNode.x}, {startNode.y} to {endNode.x}, {endNode.y}");
                    try
                    {
                        StartNodes[threadIndex] = GridNodes[threadIndex][startX][startY];
                        EndNodes[threadIndex] = GridNodes[threadIndex][endX][endY];

                    }
                    catch (Exception e)
                    {
                        Debug.Log($"Tried to start at ({startX}, {startY}) and end at ({endX}, {endY}) for {ship.Name} on thread #{threadIndex}");
                        throw e;
                    }

                    //StartNodes[threadIndex] = GridNodes[threadIndex][startX][startY];
                    //EndNodes[threadIndex] = GridNodes[threadIndex][endX][endY];

                    Ships[threadIndex] = ship;

                    BTFindPath(threadIndex);
                    //Debug.Log($"Standard Started BT  #{threadIndex}:{Ships[threadIndex].Name} ");
                    //Debug.Log($"Standard Started #{i}");
                    startedTask = true;
                    if (ShipsQueued.Contains(ship))
                    {
                        ShipsToDequeue.Add(ship);
                    }
                    //ThreadsStarted++;
                    //PathsWaitingToRemove.Add(p);
                    break;
                }
                else
                {
                    if (Totals[threadIndex] != null && Totals[threadIndex].ElapsedMilliseconds > 1000)
                    {
                        //Debug.Log($"Thread #{threadIndex}:{Ships[threadIndex].Name} has been running for {Totals[threadIndex].ElapsedMilliseconds}ms");
                    }
                    //Debug.Log($"Thread #{i} has been running for {Totals[i].ElapsedMilliseconds}ms");
                }
            }
            if (!startedTask)
            {
                if (!ShipsQueued.Contains(ship))
                {
                    PathsWaiting.Enqueue(new PathWaiting(ship, startX, startY, endX, endY, maximumClearance, requestId));
                    ShipsQueued.Add(ship);
                }
            }


        }




        // [alert] only works for rectanglular maps
        /// <summary>
        /// A two-dimensional array of map nodes
        /// </summary>
        public class Grid
        {
            public int Width, Height;
            /// <summary>
            /// The maximum x and y values that are valid indexes. One less than the height and width
            /// </summary>
            public int MaxX, MaxY;
            public int TotalNodes;
            public HashSet<MapNode> NodeSet = new HashSet<MapNode>();
            public MapNode[][] Nodes;
            public Pathfinder Pathfinder;

            public Grid(int width, int height, Pathfinder pathfinder)
            {
                Width = width;
                Height = height;
                MaxX = Width - 1;
                MaxY = Height - 1;
                Pathfinder = pathfinder;
                Nodes = new MapNode[width][];

                for (int x = 0; x < Width; x++)
                {
                    Nodes[x] = new MapNode[Height];
                    for (int y = 0; y < Height; y++)
                    {
                        Nodes[x][y] = new MapNode(x, y, this, TotalNodes++);
                        TotalNodes++;
                        NodeSet.Add(Nodes[x][y]);
                        //if (!NodeSet.Contains(Nodes[x][y])) // [debug]
                        //{
                        //    NodeSet.Add(Nodes[x][y]);
                        //}
                        //else
                        //{
                        //    Debug.LogError($"{Nodes[x][y]} has already been added to the grid! {NodeSet.Where((node) => node == Nodes[x][y]).First()}");
                        //}
                    }
                }
            }
            public void Reset()
            {
                for (int x = 0; x < Width; x++)
                {
                    for (int y = 0; y < Height; y++)
                    {
                        Nodes[x][y].Clearance = 1;
                        Nodes[x][y].OriginalClearance = 1;
                        Nodes[x][y].CostToHere = int.MaxValue;
                        Nodes[x][y].TotalCost = int.MaxValue;
                        Nodes[x][y].PreviousNode = MapNode.NullNode;
                        Nodes[x][y].HasBeenChecked = false;
                        Nodes[x][y].IsPartOfPath = false;
                    }
                }
            }
            public void DebugGridAsImage(Vector2Int firstNode, Vector2Int lastNode, MapNode[][] nodes, int scale, Ship ship)
            {
                Texture2D texture = new Texture2D(Width * scale, Height * scale, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                MapNode node;
                for (int y = 0; y < Height * scale; y += scale)
                {
                    for (int x = 0; x < Width * scale; x += scale)
                    {
                        node = nodes[x / scale][y / scale];
                        float darkness = 5.0f;
                        Color color = new Color(node.Clearance / darkness, node.Clearance / darkness, node.Clearance / darkness); // has not been checked
                        if (node.Clearance >= ship.GetClearance())
                        {
                            color = Color.green;
                        }

                        if (node.Clearance == 0) // obstacle
                        {
                            color = ConfigData.GetUIColor("bad");
                        }
                        else if (node.IsPartOfPath) // not an obstacle, checked, and part of the path
                        {
                            color = ConfigData.GetUIColor("medium");
                        }
                        else if (node.HasBeenChecked) // not an obstacle, checked, and not part of the path
                        {
                            color = Color.cyan;
                        }

                        if (ship.DebugWalkablePointNodes.Contains(node))
                        {
                            color = new Color(.94f, .59f, .29f, 1); // orange
                        }


                        for (int v = 0; v < scale; v++)
                        {
                            for (int h = 0; h < scale; h++)
                            {
                                texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                            }
                        }

                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((firstNode.x * scale) + h, (Height * scale) - ((firstNode.y * scale) + (1 - v)), Color.red); // first pixel
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((lastNode.x * scale) + h, (Height * scale) - ((lastNode.y * scale) + (1 - v)), Color.blue); // last pixel
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((ship.DebugOriginalStartNode.x * scale) + h, (Height * scale) - ((ship.DebugOriginalStartNode.y * scale) + (1 - v)), Color.magenta); // original first pixel
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((ship.DebugOriginalEndNode.x * scale) + h, (Height * scale) - ((ship.DebugOriginalEndNode.y * scale) + (1 - v)), Color.white); // original last pixel
                    }
                }

                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/debug/{ship.ShipType}_CL{ship.GetClearance()}_T{ship.PathfindingThread}_{Utilities.Hash()}.png";
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
        }
        public class MapNode : IComparable<MapNode>
        {
            public static MapNode NullNode = new MapNode(-1, -1, null, -1);
            public int CostToHere; // The g cost
            public int HueristicCost; // The h cost
            public int TotalCost; // The f cost
            /// <summary>
            /// The x and y indices of the map node in the grid
            /// </summary>
            public int x, y;
            public Vector2Int Index;
            public readonly int Id;
            //public int SortingId;
            public int OriginalClearance;
            public int Clearance;
            public bool HasBeenChecked;
            public bool IsPartOfPath;
            public MapNode PreviousNode = NullNode;
            public List<MapNode> Neighbors;
            public Grid Grid;

            /// <summary>
            /// The Level coordinates of the Node
            /// </summary>
            public Vector2 Vector;
            public Vector2Int VectorInt;

            public MapNode(int x, int y, Grid grid, int id)
            {
                this.x = x;
                this.y = y;
                Index = new Vector2Int(x, y);
                if (grid != null)
                {
                    Grid = grid;
                    Vector = Grid.Pathfinder.ConvertToLevelCoordinates(x, y);
                    VectorInt = Grid.Pathfinder.ConvertToLevelCoordinatesInt(x, y);
                }
                Id = id;


                Clearance = 1;
                OriginalClearance = Clearance;
                //Id = x >= y ? x * x + x + y : x + y * y;  // Szudzik's function
            }
            public int DistanceTo(MapNode node)
            {
                return CalculateDistance(this, node);
            }
            public static int CalculateDistance(MapNode a, MapNode b)
            {
                int xDistance = Mathf.Abs(a.x - b.x);
                int yDistance = Mathf.Abs(a.y - b.y);
                return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * Mathf.Abs(xDistance - yDistance);

            }

            public List<MapNode> GetNeighbors(MapNode[][] nodes)
            {
                if (Neighbors != null)
                {
                    return Neighbors;
                }

                Neighbors = new List<MapNode>(8);

                if (x - 1 >= 0) // There is space to the left
                {

                    Neighbors.Add(nodes[x - 1][y]); // get left neighbor

                    if (y - 1 >= 0)
                    {
                        Neighbors.Add(nodes[x - 1][y - 1]); // get bottom left neighbor
                    }

                    if (y + 1 < Grid.Height)
                    {
                        Neighbors.Add(nodes[x - 1][y + 1]); // get top left neighbor
                    }

                }
                if (x + 1 < Grid.Width) // There is space to the right
                {

                    Neighbors.Add(nodes[x + 1][y]); // get right neighbor

                    if (y - 1 >= 0)
                    {
                        Neighbors.Add(nodes[x + 1][y - 1]); // get bottom right neighbor
                    }

                    if (y + 1 < Grid.Height)
                    {
                        Neighbors.Add(nodes[x + 1][y + 1]); // get top right neighbor
                    }

                }

                if (y - 1 >= 0) // there is space below
                {
                    Neighbors.Add(nodes[x][y - 1]);
                }

                if (y + 1 < Grid.Height) // there is space above
                {
                    Neighbors.Add(nodes[x][y + 1]);
                }


                return Neighbors;
            }
            public void CalculateTotalCost()
            {
                TotalCost = CostToHere + HueristicCost;
                //SortingId = (TotalCost * 10000000) + Id;
            }
            public override bool Equals(System.Object obj)
            {
                //Debug.Log(".Equals()");
                //// If parameter is null return false.
                //if (obj == null)
                //{
                //    return false;
                //}

                //// If parameter cannot be cast to MapNode  return false
                //MapNode p = obj as MapNode;
                //if (p == null)
                //{
                //    return false;
                //}

                // Return true if the fields match:
                return this == ((MapNode) obj);
            }
            //public static int equalsCalls = 0; // 748295
            public static bool operator ==(MapNode a, MapNode b)
            {
                //equalsCalls++;
                //Debug.Log($" == {equalsCalls}");
                // If both are null, or both are same instance, return true.
                //if (System.Object.ReferenceEquals(a, b))
                //{
                //    return true;
                //}

                //// If one is null, but not both, return false.
                //if (((object)a == null) || ((object)b == null))
                //{
                //    return false;
                //}

                // Return true if the fields match:
                return a.Id == b.Id;

            }
            public static bool operator !=(MapNode a, MapNode b)
            {
                return !(a == b);
            }
            public bool Equals(MapNode other)
            {
                return this == other;
            }
            public override int GetHashCode()
            {
                return Id;
            }
            public int CompareTo(System.Object other)
            {
                //if ((MapNode)other == NullNode)
                //{
                //    return -1;
                //}
                return CompareTo((MapNode)other);

            }
            public int CompareTo(MapNode other)
            {
                return Id.CompareTo(other.Id);
            }
            protected string Info()
            {
                return $"MapNode #{Id}: ({x}, {y}), PreviousNode: {PreviousNode.Id}, Clearance: {Clearance}, IN: {(Neighbors == null ? 0 : Neighbors.Count)}\n";
            }
            public override string ToString()
            {
                if (Grid == null)
                {
                    return Info();
                }
                GetNeighbors(GridNodesSafe());
                string toString = Info();

                toString += $"Neighbors ({Neighbors.Count}):\n\n";
                for (int i = 0; i < Neighbors.Count && i < 10; i++)
                {
                    toString += Neighbors.ElementAt(i).Info();
                }
                return toString;

            }
            public string ToJson()
            {
                if (Grid == null)
                {
                    return "{}";
                }
                GetNeighbors(GridNodesSafe());
                string json = $"{{\"Id\": {Id}, \"x\": {x}, \"y\": {y}, \"OC\": {OriginalClearance}, \"N\": [";
                Neighbors.ForEach((n) => json += $" {{ \"Id\": {n.Id}, \"x\": {n.x}, \"y\": {n.y}, \"OC\": {n.OriginalClearance} }},");

                if (Neighbors.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }



                json += "]}";

                return json;
            }

            private MapNode[][] GridNodesSafe()
            {
                return Grid.Pathfinder.GridNodes[0];
            }
        }
        public class Path
        {
            //public static Path NullPath = new Path(-1, -1, -1, -1);
            public List<Vector2> Points;
            public int StartX, StartY, EndX, EndY;
            public long Id;
            public bool IsCached;

            public Path(int startX, int startY, int endX, int endY)
            {
                StartX = startX;
                StartY = startY;
                EndX = endX;
                EndY = endY;
                Id = Convert.ToInt64($"{startX}{startY}{endX}{endY}");
            }
            public void SetPoints(List<Vector2> points)
            {
                Points = points;
            }

            public override bool Equals(System.Object obj)
            {

                // If parameter is null return false.
                if (obj == null)
                {
                    return false;
                }

                // If parameter cannot be cast to Point return false.
                Path p = obj as Path;
                if (p == null)
                {
                    return false;
                }

                // Return true if the fields match:
                return Id == p.Id;
            }

            public bool Equals(Path other)
            {
                return Id == other.Id;
            }

            public override int GetHashCode()
            {
                return Id.GetHashCode();
            }

            public static bool operator ==(Path a, Path b)
            {
                // If both are null, or both are same instance, return true.
                if (System.Object.ReferenceEquals(a, b))
                {
                    return true;
                }

                // If one is null, but not both, return false.
                if (((object)a == null) || ((object)b == null))
                {
                    return false;
                }

                // Return true if the fields match:
                return a.Id == b.Id;
            }

            public static bool operator !=(Path a, Path b)
            {
                return !(a == b);
            }

            public override string ToString()
            {
                return $"Path #{Id} starting from ({StartX}, {StartY}) and going through {Points.Count} points to get to ({EndX}, {EndY})";
            }
            public string ToFile()
            {
                string json = $"{StartX},{StartY},{EndX},{EndY},";
                Points.ForEach((p) => json += $"{p.x},{p.y},");
                json = json.Substring(0, json.Length - 1);
                return json;
            }
        }
    }

}

