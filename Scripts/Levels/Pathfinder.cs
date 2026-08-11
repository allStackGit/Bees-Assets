using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
        public const int DIAGONAL_COST = 14;
        public const int HORIZONTAL_COST = 10;
        public const float TimeLimit = 5f;
        private const int PreferredClearanceBuffer = 2;
        private const int ClearancePenaltyMultiplier = HORIZONTAL_COST;
        private Grid _grid;

        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly.
        /// </summary>
        public const int Scale = 4;
        public int Width, Height, HalfWidth, HalfHeight;
        public Level Level;
        public bool HasMovingObstacles;
        private int _totalNodes;
        private int[] _baseClearance;
        private int[] _staticSignedClearance;
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
        private bool _staticObstacleLayerDirty;
        private bool _staticObstacleRebuildPending;
        private bool[] _staticRebuildBlockedSlots = new bool[ConfigData.MaxThreads];
        private readonly ConcurrentQueue<PathResult> _completedPaths = new ConcurrentQueue<PathResult>();
        private readonly List<int> _obstaclePointIndexes = new List<int>();
        private readonly HashSet<int> _obstaclePointIndexSet = new HashSet<int>();
        private static readonly int[] NeighborX = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] NeighborY = { -1, 0, 1, -1, 1, -1, 0, 1 };

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
            InitializeMap();
        }

        public Vector2Int ConvertToMapCoordinates(Vector2 coords)
        {
            int x = (int)Math.Floor((Level.HalfMapWidth + coords.x) / Scale);
            int y = (int)Math.Floor((Level.HalfMapHeight - coords.y) / Scale);
            return new Vector2Int(Mathf.Clamp(x, 0, Width - 1), Mathf.Clamp(y, 0, Height - 1));
        }

        public Vector2 ConvertToLevelCoordinates(int x, int y)
        {
            return new Vector2((-Level.HalfMapWidth + (x * Scale)) + (Scale * 0.5f),
                (Level.HalfMapHeight - (y * Scale)) - (Scale * 0.5f));
        }

        public Vector2Int ConvertToLevelCoordinatesInt(int x, int y)
        {
            return Vector2Int.RoundToInt(ConvertToLevelCoordinates(x, y));
        }

        public bool CanOccupyDestination(Vector2 destination, int shipClearance)
        {
            if (!Level.HasObstacles)
            {
                return true;
            }
            if (Level.ForceBounds(destination) != destination)
            {
                return false;
            }

            UpdateDynamicObstacleLayer();
            Vector2Int coordinates = ConvertToMapCoordinates(destination);
            return _dynamicClearance[ToIndex(coordinates.x, coordinates.y)] >= GetEffectivePathClearance(shipClearance);
        }

        public bool TryFindNearestValidDestination(Vector2 destination, int shipClearance, out Vector2 validDestination)
        {
            destination = Level.ForceBounds(destination);
            if (!Level.HasObstacles)
            {
                validDestination = destination;
                return true;
            }

            UpdateDynamicObstacleLayer();
            int minimumClearance = GetEffectivePathClearance(shipClearance);
            Vector2Int center = ConvertToMapCoordinates(destination);
            int maxRadius = Mathf.Max(Width, Height);

            for (int radius = 0; radius <= maxRadius; radius++)
            {
                int minX = Mathf.Max(0, center.x - radius);
                int maxX = Mathf.Min(_grid.MaxX, center.x + radius);
                int minY = Mathf.Max(0, center.y - radius);
                int maxY = Mathf.Min(_grid.MaxY, center.y + radius);
                bool found = false;
                float bestDistance = float.MaxValue;
                Vector2 bestDestination = destination;

                for (int x = minX; x <= maxX; x++)
                {
                    CheckDestinationCandidate(x, minY, destination, minimumClearance, ref found, ref bestDistance, ref bestDestination);
                    if (maxY != minY)
                    {
                        CheckDestinationCandidate(x, maxY, destination, minimumClearance, ref found, ref bestDistance, ref bestDestination);
                    }
                }
                for (int y = minY + 1; y < maxY; y++)
                {
                    CheckDestinationCandidate(minX, y, destination, minimumClearance, ref found, ref bestDistance, ref bestDestination);
                    if (maxX != minX)
                    {
                        CheckDestinationCandidate(maxX, y, destination, minimumClearance, ref found, ref bestDistance, ref bestDestination);
                    }
                }

                if (found)
                {
                    validDestination = bestDestination;
                    return true;
                }
            }

            validDestination = destination;
            return false;
        }

        private void CheckDestinationCandidate(int x, int y, Vector2 requestedDestination, int minimumClearance,
            ref bool found, ref float bestDistance, ref Vector2 bestDestination)
        {
            if (_dynamicClearance[ToIndex(x, y)] < minimumClearance)
            {
                return;
            }

            Vector2 candidate = ConvertToLevelCoordinates(x, y);
            float distance = (candidate - requestedDestination).sqrMagnitude;
            if (!found || distance < bestDistance)
            {
                found = true;
                bestDistance = distance;
                bestDestination = candidate;
            }
        }

        public void MarkObstacleLayerDirty()
        {
            EnsureStaticRebuildBlockedSlots();
            if (HasActivePathWorkers())
            {
                // RebuildStaticObstacleLayer rewrites the shared base maps and every thread's
                // clearance buffer. Never run it while a Task.Run search is reading one of those
                // snapshots. Reserve otherwise-idle slots too so new searches queue until the
                // current workers finish and the rebuild has been applied atomically on main.
                _staticObstacleRebuildPending = true;
                _staticObstacleLayerDirty = false;
                for (int i = 0; i < IsThreadActive.Length; i++)
                {
                    if (!IsThreadActive[i])
                    {
                        IsThreadActive[i] = true;
                        _staticRebuildBlockedSlots[i] = true;
                    }
                }
            }
            else
            {
                _staticObstacleLayerDirty = true;
            }
            _dynamicLayerFrame = -1;
        }

        private void EnsureStaticRebuildBlockedSlots()
        {
            if (_staticRebuildBlockedSlots == null || _staticRebuildBlockedSlots.Length != IsThreadActive.Length)
            {
                _staticRebuildBlockedSlots = new bool[IsThreadActive.Length];
            }
        }

        private bool HasActivePathWorkers()
        {
            if (IsThreadActive == null)
            {
                return false;
            }

            EnsureStaticRebuildBlockedSlots();
            for (int i = 0; i < IsThreadActive.Length; i++)
            {
                if (IsThreadActive[i] && !_staticRebuildBlockedSlots[i])
                {
                    return true;
                }
            }
            return false;
        }

        private bool PreparePendingStaticObstacleRebuild()
        {
            if (!_staticObstacleRebuildPending)
            {
                return true;
            }
            if (HasActivePathWorkers())
            {
                return false;
            }

            for (int i = 0; i < _staticRebuildBlockedSlots.Length; i++)
            {
                if (_staticRebuildBlockedSlots[i])
                {
                    _staticRebuildBlockedSlots[i] = false;
                    IsThreadActive[i] = false;
                }
            }
            _staticObstacleRebuildPending = false;
            _staticObstacleLayerDirty = true;
            return true;
        }

        private int ToIndex(int x, int y) => (y * Width) + x;
        private int ToX(int index) => index % Width;
        private int ToY(int index) => index / Width;

        public void Update()
        {
            ApplyCompletedPathResults();

            if (!PreparePendingStaticObstacleRebuild())
            {
                return;
            }
            if (_staticObstacleLayerDirty)
            {
                UpdateDynamicObstacleLayer();
            }

            if (PathsWaiting.Count <= 0)
            {
                return;
            }

            for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads && PathsWaiting.Count > 0; threadIndex++)
            {
                if (!IsThreadActive[threadIndex])
                {
                    PathWaiting p = PathsWaiting.Dequeue();
                    ReleaseQueuedShipIfNoRemainingRequests(p.Ship);
                    if (p.Ship == null)
                    {
                        continue;
                    }
                    if (p.Ship.PathfindingLifecycleId != p.LifecycleId)
                    {
                        p.Ship.HandleSupersededPathfindingRequest();
                        continue;
                    }
                    if (p.Ship.PathfindingRequestId != p.RequestId)
                    {
                        p.Ship.HandleSupersededPathfindingRequest();
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
                    LifecycleIds[threadIndex] = p.LifecycleId;
                    BTFindPath(threadIndex);
                }
                else if (Totals[threadIndex] != null && Totals[threadIndex].ElapsedMilliseconds > 1000)
                {
                    Debug.Log($"Thread #{threadIndex}:{Ships[threadIndex].Name} has been running for {Totals[threadIndex].ElapsedMilliseconds}ms");
                }
            }
        }

        private int x, y;
        private MapNode currentNode;

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
                        currentNode.Clearance = Mathf.Min(Mathf.Min(clearanceCap, x + 1),
                            Mathf.Min(y + 1, Mathf.Min(_grid.Width - x, _grid.Height - y)));
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
                    if (x > 0) clearance = Mathf.Min(clearance, nodes[x - 1][y].Clearance + 1);
                    if (y > 0) clearance = Mathf.Min(clearance, nodes[x][y - 1].Clearance + 1);
                    if (x > 0 && y > 0) clearance = Mathf.Min(clearance, nodes[x - 1][y - 1].Clearance + 1);
                    if (x < _grid.MaxX && y > 0) clearance = Mathf.Min(clearance, nodes[x + 1][y - 1].Clearance + 1);
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
                    if (x < _grid.MaxX) clearance = Mathf.Min(clearance, nodes[x + 1][y].Clearance + 1);
                    if (y < _grid.MaxY) clearance = Mathf.Min(clearance, nodes[x][y + 1].Clearance + 1);
                    if (x < _grid.MaxX && y < _grid.MaxY) clearance = Mathf.Min(clearance, nodes[x + 1][y + 1].Clearance + 1);
                    if (x > 0 && y < _grid.MaxY) clearance = Mathf.Min(clearance, nodes[x - 1][y + 1].Clearance + 1);
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
                ApplyCompletedPathResult(result.Ship, result.RequestId, result.LifecycleId, result.ThreadIndex, result.Path);
            }
        }

        private void ApplyCompletedPathResult(Ship ship, int requestId, int lifecycleId, int threadIndex, Path path)
        {
            if (threadIndex < 0 || threadIndex >= IsThreadActive.Length)
            {
                return;
            }

            bool ownsThreadSlot = ReferenceEquals(Ships[threadIndex], ship) &&
                RequestIds[threadIndex] == requestId &&
                LifecycleIds[threadIndex] == lifecycleId;
            if (!ownsThreadSlot)
            {
                return;
            }

            IsThreadActive[threadIndex] = false;
            Ships[threadIndex] = null;
            RequestIds[threadIndex] = 0;
            LifecycleIds[threadIndex] = 0;

            if (ship == null || ship.PathfindingLifecycleId != lifecycleId)
            {
                return;
            }
            if (ship.PathfindingRequestId != requestId)
            {
                ship.HandleSupersededPathfindingRequest();
                return;
            }

            ship.PathfindingValue = path;
            ship.PathfindingCompletedRequestId = requestId;
            ship.PathfindingThreadComplete = true;
        }

        private void CalculateClearance(int[] clearanceMap, int maxClearance)
        {
            int clearanceCap = maxClearance == int.MaxValue ? Mathf.Max(Width, Height) : maxClearance;

            for (int row = 0; row < Height; row++)
            {
                for (int column = 0; column < Width; column++)
                {
                    int index = ToIndex(column, row);
                    if (clearanceMap[index] > 0)
                    {
                        clearanceMap[index] = Mathf.Min(Mathf.Min(clearanceCap, column + 1),
                            Mathf.Min(row + 1, Mathf.Min(Width - column, Height - row)));
                    }
                }
            }

            for (int row = 0; row < Height; row++)
            {
                for (int column = 0; column < Width; column++)
                {
                    int index = ToIndex(column, row);
                    if (clearanceMap[index] == 0)
                    {
                        continue;
                    }

                    int clearance = clearanceMap[index];
                    if (column > 0) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column - 1, row)] + 1);
                    if (row > 0) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column, row - 1)] + 1);
                    if (column > 0 && row > 0) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column - 1, row - 1)] + 1);
                    if (column < _grid.MaxX && row > 0) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column + 1, row - 1)] + 1);
                    clearanceMap[index] = clearance;
                }
            }

            for (int row = Height - 1; row >= 0; row--)
            {
                for (int column = Width - 1; column >= 0; column--)
                {
                    int index = ToIndex(column, row);
                    if (clearanceMap[index] == 0)
                    {
                        continue;
                    }

                    int clearance = clearanceMap[index];
                    if (column < _grid.MaxX) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column + 1, row)] + 1);
                    if (row < _grid.MaxY) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column, row + 1)] + 1);
                    if (column < _grid.MaxX && row < _grid.MaxY) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column + 1, row + 1)] + 1);
                    if (column > 0 && row < _grid.MaxY) clearance = Mathf.Min(clearance, clearanceMap[ToIndex(column - 1, row + 1)] + 1);
                    clearanceMap[index] = clearance;
                }
            }
        }
    }
}
