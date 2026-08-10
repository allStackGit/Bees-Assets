using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SW = System.Diagnostics;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
        private int[] BuildStaticSignedClearance(int[] staticClearance)
        {
            int[] signedClearance = new int[_totalNodes];
            const int unreachable = int.MaxValue / 4;
            for (int i = 0; i < _totalNodes; i++)
            {
                signedClearance[i] = staticClearance[i] == 0 ? unreachable : 0;
            }

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = ToIndex(x, y);
                    if (signedClearance[index] == 0)
                    {
                        continue;
                    }

                    int distance = signedClearance[index];
                    if (x > 0)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x - 1, y)] + 1);
                    }
                    if (y > 0)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x, y - 1)] + 1);
                    }
                    if (x > 0 && y > 0)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x - 1, y - 1)] + 1);
                    }
                    if (x < _grid.MaxX && y > 0)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x + 1, y - 1)] + 1);
                    }
                    signedClearance[index] = distance;
                }
            }

            for (int y = Height - 1; y >= 0; y--)
            {
                for (int x = Width - 1; x >= 0; x--)
                {
                    int index = ToIndex(x, y);
                    if (signedClearance[index] == 0)
                    {
                        continue;
                    }

                    int distance = signedClearance[index];
                    if (x < _grid.MaxX)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x + 1, y)] + 1);
                    }
                    if (y < _grid.MaxY)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x, y + 1)] + 1);
                    }
                    if (x < _grid.MaxX && y < _grid.MaxY)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x + 1, y + 1)] + 1);
                    }
                    if (x > 0 && y < _grid.MaxY)
                    {
                        distance = Mathf.Min(distance, signedClearance[ToIndex(x - 1, y + 1)] + 1);
                    }
                    signedClearance[index] = distance;
                }
            }

            for (int i = 0; i < _totalNodes; i++)
            {
                signedClearance[i] = staticClearance[i] > 0 ? staticClearance[i] : -signedClearance[i];
            }

            return signedClearance;
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
        public HashSet<Ship> ShipsQueued = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public HashSet<Ship> ShipsToDequeue = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public bool[] IsThreadActive = new bool[ConfigData.MaxThreads];
        public List<int[][]>[] ObstaclePoints = new List<int[][]>[ConfigData.MaxThreads];

        public SW.Stopwatch[] Totals = new SW.Stopwatch[ConfigData.MaxThreads];
        public MapNode[] StartNodes = new MapNode[ConfigData.MaxThreads];
        public MapNode[] EndNodes = new MapNode[ConfigData.MaxThreads];
        public int[] Clearances = new int[ConfigData.MaxThreads];
        public int[] RequestIds = new int[ConfigData.MaxThreads];
        public int[] LifecycleIds = new int[ConfigData.MaxThreads];
        public Ship[] Ships = new Ship[ConfigData.MaxThreads];
        public MapNode[][][] GridNodes = new MapNode[ConfigData.MaxThreads][][];

        public class PathWaiting
        {
            public Ship Ship;
            public int Clearance, StartX, StartY, EndX, EndY, RequestId, LifecycleId;
            public float StartTime = Time.realtimeSinceStartup;

            public PathWaiting(Ship ship, int startX, int startY, int endX, int endY, int clearance, int requestId, int lifecycleId)
            {
                Ship = ship;
                StartX = startX;
                StartY = startY;
                EndX = endX;
                EndY = endY;
                Clearance = clearance;
                RequestId = requestId;
                LifecycleId = lifecycleId;
            }
        }

        private class PathResult
        {
            public Ship Ship;
            public int RequestId, LifecycleId, ThreadIndex;
            public Path Path;

            public PathResult(Ship ship, int requestId, int lifecycleId, int threadIndex, Path path)
            {
                Ship = ship;
                RequestId = requestId;
                LifecycleId = lifecycleId;
                ThreadIndex = threadIndex;
                Path = path;
            }
        }

        private Path RunPathSearch(int threadIndex)
        {
            int hardClearance = GetEffectivePathClearance(Clearances[threadIndex]);
            int preferredClearance = GetPreferredPathClearance(hardClearance);
            int originalStartIndex = ToIndex(StartNodes[threadIndex].x, StartNodes[threadIndex].y);
            int startIndex = originalStartIndex;
            int endIndex = ToIndex(EndNodes[threadIndex].x, EndNodes[threadIndex].y);
            int[] clearanceMap = _threadClearance[threadIndex];
            int[] staticSignedClearance = _staticSignedClearance;
            List<int> egressIndexes = null;

            if (clearanceMap[endIndex] < hardClearance)
            {
                return null;
            }

            if (clearanceMap[startIndex] < hardClearance)
            {
                egressIndexes = FindStaticEgressPath(startIndex, hardClearance, clearanceMap, staticSignedClearance, threadIndex);
                if (egressIndexes == null || egressIndexes.Count == 0)
                {
                    return null;
                }
                startIndex = egressIndexes[egressIndexes.Count - 1];
            }

            int searchStamp = BeginSearch(threadIndex);

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
                    return MakeDestinationList(originalStartIndex, startIndex, endIndex, previousIndex, clearanceMap, hardClearance, preferredClearance, egressIndexes);
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
                        clearanceMap[neighborIndex] < hardClearance ||
                        IsDiagonalMoveBlocked(currentX, currentY, neighborX, neighborY, hardClearance, clearanceMap))
                    {
                        continue;
                    }

                    int newCostToHere = costToHere[currentIndex] + CalculateDistance(currentIndex, neighborIndex) + GetClearanceCost(clearanceMap[neighborIndex], preferredClearance);
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

        private int BeginSearch(int threadIndex)
        {
            int searchStamp = ++_searchStamp[threadIndex];
            if (searchStamp == int.MaxValue)
            {
                Array.Clear(_openStamp[threadIndex], 0, _totalNodes);
                Array.Clear(_closedStamp[threadIndex], 0, _totalNodes);
                searchStamp = _searchStamp[threadIndex] = 1;
            }
            return searchStamp;
        }

        private List<int> FindStaticEgressPath(int startIndex, int hardClearance, int[] clearanceMap, int[] staticSignedClearance, int threadIndex)
        {
            if (staticSignedClearance == null || staticSignedClearance[startIndex] >= hardClearance)
            {
                return null;
            }

            int searchStamp = BeginSearch(threadIndex);
            int[] costs = _costToHere[threadIndex];
            int[] tieBreakers = _heuristicCost[threadIndex];
            int[] previousIndex = _previousIndex[threadIndex];
            int[] openStamp = _openStamp[threadIndex];
            int[] closedStamp = _closedStamp[threadIndex];
            IntMinHeap open = new IntMinHeap(costs, tieBreakers);

            costs[startIndex] = 0;
            tieBreakers[startIndex] = 0;
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

                if (staticSignedClearance[currentIndex] >= hardClearance && clearanceMap[currentIndex] >= hardClearance)
                {
                    return ReconstructIndexes(startIndex, currentIndex, previousIndex);
                }

                closedStamp[currentIndex] = searchStamp;
                int currentX = ToX(currentIndex);
                int currentY = ToY(currentIndex);
                int currentSignedClearance = staticSignedClearance[currentIndex];
                int currentCombinedClearance = clearanceMap[currentIndex];

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
                        staticSignedClearance[neighborIndex] < currentSignedClearance ||
                        clearanceMap[neighborIndex] < currentCombinedClearance ||
                        IsEgressDiagonalBlocked(currentX, currentY, neighborX, neighborY, currentSignedClearance, currentCombinedClearance, staticSignedClearance, clearanceMap))
                    {
                        continue;
                    }

                    int newCost = costs[currentIndex] + CalculateDistance(currentIndex, neighborIndex);
                    if (openStamp[neighborIndex] != searchStamp || newCost < costs[neighborIndex])
                    {
                        costs[neighborIndex] = newCost;
                        tieBreakers[neighborIndex] = -staticSignedClearance[neighborIndex];
                        previousIndex[neighborIndex] = currentIndex;
                        openStamp[neighborIndex] = searchStamp;
                        open.Push(neighborIndex);
                    }
                }
            }

            return null;
        }

        private bool IsEgressDiagonalBlocked(int currentX, int currentY, int neighborX, int neighborY, int minimumSignedClearance, int minimumCombinedClearance, int[] staticSignedClearance, int[] clearanceMap)
        {
            if (Mathf.Abs(currentX - neighborX) != 1 || Mathf.Abs(currentY - neighborY) != 1)
            {
                return false;
            }

            int firstSide = ToIndex(currentX, neighborY);
            int secondSide = ToIndex(neighborX, currentY);
            return staticSignedClearance[firstSide] < minimumSignedClearance ||
                   staticSignedClearance[secondSide] < minimumSignedClearance ||
                   clearanceMap[firstSide] < minimumCombinedClearance ||
                   clearanceMap[secondSide] < minimumCombinedClearance;
        }

        private List<int> ReconstructIndexes(int startIndex, int endIndex, int[] previousIndex)
        {
            List<int> indexes = new List<int> { endIndex };
            int currentIndex = endIndex;
            while (currentIndex != startIndex && previousIndex[currentIndex] >= 0)
            {
                currentIndex = previousIndex[currentIndex];
                indexes.Add(currentIndex);
            }
            indexes.Reverse();
            return indexes;
        }

        private int GetEffectivePathClearance(int shipClearance)
        {
            return Mathf.Max(ConfigData.MinimumClearance, shipClearance);
        }

        private int GetPreferredPathClearance(int hardClearance)
        {
            return hardClearance + PreferredClearanceBuffer;
        }

        private int GetClearanceCost(int nodeClearance, int preferredClearance)
        {
            int clearanceShortfall = preferredClearance - nodeClearance;
            return clearanceShortfall > 0 ? clearanceShortfall * clearanceShortfall * ClearancePenaltyMultiplier : 0;
        }

        private Path MakeDestinationList(int originalStartIndex, int startIndex, int endIndex, int[] previousIndex, int[] clearanceMap, int hardClearance, int preferredClearance, List<int> egressIndexes)
        {
            Path path = new Path(ToX(originalStartIndex), ToY(originalStartIndex), ToX(endIndex), ToY(endIndex));
            List<int> normalIndexes = ReconstructIndexes(startIndex, endIndex, previousIndex);
            normalIndexes = SmoothPathIndexes(normalIndexes, clearanceMap, hardClearance, preferredClearance);

            List<int> indexes;
            if (egressIndexes != null)
            {
                indexes = new List<int>(egressIndexes.Count + normalIndexes.Count - 1);
                indexes.AddRange(egressIndexes);
                for (int i = 1; i < normalIndexes.Count; i++)
                {
                    indexes.Add(normalIndexes[i]);
                }
            }
            else
            {
                indexes = normalIndexes;
            }

            if (indexes.Count > 1)
            {
                indexes.RemoveAt(0);
            }
            path.EgressPointCount = egressIndexes == null ? 0 : Mathf.Max(0, egressIndexes.Count - 1);

            List<Vector2> points = new List<Vector2>();
            for (int i = 0; i < indexes.Count; i++)
            {
                points.Add(ConvertToLevelCoordinates(ToX(indexes[i]), ToY(indexes[i])));
            }

            path.Points = points;
            return path;
        }

        private List<int> SmoothPathIndexes(List<int> indexes, int[] clearanceMap, int hardClearance, int preferredClearance)
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
                while (next > current + 1 && !HasClearGridLine(indexes[current], indexes[next], clearanceMap, preferredClearance))
                {
                    next--;
                }

                if (next == current + 1 && !HasClearGridLine(indexes[current], indexes[next], clearanceMap, preferredClearance))
                {
                    next = indexes.Count - 1;
                    while (next > current + 1 && !HasClearGridLine(indexes[current], indexes[next], clearanceMap, hardClearance))
                    {
                        next--;
                    }
                }

                if (!HasClearGridLine(indexes[current], indexes[next], clearanceMap, hardClearance))
                {
                    next = current + 1;
                }

                smoothed.Add(indexes[next]);
                current = next;
            }

            return smoothed;
        }

        private bool HasClearGridLine(int startIndex, int endIndex, int[] clearanceMap, int clearance)
        {
            int x = ToX(startIndex);
            int y = ToY(startIndex);
            int endX = ToX(endIndex);
            int endY = ToY(endIndex);
            int dx = endX - x;
            int dy = endY - y;
            int stepsX = Mathf.Abs(dx);
            int stepsY = Mathf.Abs(dy);
            int directionX = Math.Sign(dx);
            int directionY = Math.Sign(dy);
            int movedX = 0;
            int movedY = 0;

            if (!IsClearGridCell(x, y, clearanceMap, clearance))
            {
                return false;
            }

            while (movedX < stepsX || movedY < stepsY)
            {
                int decision = ((1 + (2 * movedX)) * stepsY) - ((1 + (2 * movedY)) * stepsX);
                if (decision == 0)
                {
                    if (!IsClearGridCell(x + directionX, y, clearanceMap, clearance) ||
                        !IsClearGridCell(x, y + directionY, clearanceMap, clearance))
                    {
                        return false;
                    }
                    x += directionX;
                    y += directionY;
                    movedX++;
                    movedY++;
                }
                else if (decision < 0)
                {
                    x += directionX;
                    movedX++;
                }
                else
                {
                    y += directionY;
                    movedY++;
                }

                if (!IsClearGridCell(x, y, clearanceMap, clearance))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsClearGridCell(int x, int y, int[] clearanceMap, int clearance)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height && clearanceMap[ToIndex(x, y)] >= clearance;
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
            Ship ship = Ships[threadIndex];
            int requestId = RequestIds[threadIndex];
            int lifecycleId = LifecycleIds[threadIndex];
            try
            {
                await Task.Run(() =>
                {
                    Totals[threadIndex] = SW.Stopwatch.StartNew();
                    Path path;
                    try
                    {
                        path = RunPathSearch(threadIndex);
                    }
                    finally
                    {
                        Totals[threadIndex].Stop();
                    }
                    _completedPaths.Enqueue(new PathResult(ship, requestId, lifecycleId, threadIndex, path));
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _completedPaths.Enqueue(new PathResult(ship, requestId, lifecycleId, threadIndex, null));
            }
        }

        public void InvalidatePathRequest(Ship ship)
        {
            ship.PathfindingRequestId = ++_nextRequestId;
        }

        public void FindPath(Ship ship, int startX, int startY, int endX, int endY, int maximumClearance)
        {
            bool startedTask = false;
            int requestId = ++_nextRequestId;
            int lifecycleId = ship.PathfindingLifecycleId;
            ship.PathfindingRequestId = requestId;
            ship.IsPathfinding = true;
            startX = Mathf.Clamp(startX, 0, _grid.MaxX);
            startY = Mathf.Clamp(startY, 0, _grid.MaxY);
            endX = Mathf.Clamp(endX, 0, _grid.MaxX);
            endY = Mathf.Clamp(endY, 0, _grid.MaxY);

            for (int threadIndex = 0; threadIndex < ConfigData.MaxThreads; threadIndex++)
            {
                if (!IsThreadActive[threadIndex])
                {
                    IsThreadActive[threadIndex] = true;
                    Clearances[threadIndex] = maximumClearance;
                    RequestIds[threadIndex] = requestId;
                    LifecycleIds[threadIndex] = lifecycleId;
                    UpdateMap(threadIndex, ship);
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

                    Ships[threadIndex] = ship;
                    BTFindPath(threadIndex);
                    startedTask = true;
                    break;
                }
            }

            if (!startedTask)
            {
                QueuePathRequest(new PathWaiting(ship, startX, startY, endX, endY, maximumClearance, requestId, lifecycleId));
            }
        }

        private void QueuePathRequest(PathWaiting request)
        {
            PathsWaiting.Enqueue(request);
            ShipsQueued.Add(request.Ship);
        }

        private void ReleaseQueuedShipIfNoRemainingRequests(Ship ship)
        {
            if (!PathsWaiting.Any(request => ReferenceEquals(request.Ship, ship)))
            {
                ShipsQueued.Remove(ship);
            }
        }
    }
}
