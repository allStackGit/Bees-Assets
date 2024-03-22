using Assets.Scripts.Entities;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level
{
    public class Pathfinder

    {
        public const int DIAGONAL_COST = 14;
        public const int HORIZONTAL_COST = 10;
        public List<MapNode> UncheckedNodes;
        public HashSet<MapNode> CheckedNodes = new HashSet<MapNode>();
        public HashSet<Path> PathCache = new HashSet<Path>();
        public HashSet<Vector2Int> FreeAreas = new HashSet<Vector2Int>(); 
        public double TimeLimit = .5f;
        
        private Grid _grid;
        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly
        /// </summary>
        private int _scale = 5;
        private int _padding = 2;
        public int Width, Height, HalfWidth, HalfHeight;
        public int ObstacleCount;
        public LevelStage Level;
        public List<Obstacle> Obstacles = new List<Obstacle>();
        /// <summary>
        /// A list of indexes of obstacles that need to be updated next time the map updates
        /// </summary>
        public List<int> ObstaclesToUpdate = new List<int>();
        /// <summary>
        /// A list of arrays of obstacle points. Each array of points belongs to an obstacle and each point (a two int array) is an x index and a y index on the Map array
        /// </summary>
        public List<int[][]> ObstaclePoints = new List<int[][]>();
        public bool NeedsToBeUpdated;
        public bool HasMovingObstacles;

        public Pathfinder(LevelStage level)
        {
            Level = level;

            Width = Convert.ToInt32(Level.MapWidth / _scale);
            Height = Convert.ToInt32(Level.MapHeight / _scale);
            HalfWidth = Convert.ToInt32(Level.HalfMapWidth / _scale);
            HalfHeight = Convert.ToInt32(Level.HalfMapHeight / _scale);

            InitializeMap();
        }

        private void BakeMap()
        {
            int baseIncrement = 1;
            int endPoint = 10;
            for (int startX = 0; startX < endPoint; startX += baseIncrement)
            {
                for (int startY = 0; startY < endPoint; startY += baseIncrement)
                {
                    for (int endX = baseIncrement; endX < endPoint; endX += baseIncrement)
                    {
                        for (int endY = baseIncrement; endY < endPoint; endY += baseIncrement)
                        {
                            FindPath(startX, startY, endX, endY, true);
                        }
                    }
                }
            }
        }

        private int SquareSize = 10;
        private void GetMapFreeSpace()
        {
            for (int horizontalSquares = 0; horizontalSquares < (Width / SquareSize); horizontalSquares++)
            {
                for (int verticalSquares = 0; verticalSquares < (Height / SquareSize); verticalSquares++)
                {
                    //Debug.Log($"Checking square position {horizontalSquares}, {verticalSquares} for free space");
                    bool isFree = true;
                    for (int x = horizontalSquares * SquareSize; x < SquareSize * horizontalSquares + 1; x++)
                    {
                        for (int y = verticalSquares * SquareSize; y < SquareSize * verticalSquares + 1 && isFree; y++)
                        {
                            if (!_grid.Nodes[x][y].IsWalkable)
                            {
                                //Debug.Log($"Found unwalkable space at {x}, {y}, moving onto next square");
                                isFree = false;
                            }
                        }
                    }
                    if (isFree)
                    {
                        //Debug.Log($"Square position {horizontalSquares}, {verticalSquares} is completely free space!");
                        FreeAreas.Add(new Vector2Int(horizontalSquares, verticalSquares));
                    }


                }
            }
            
        }

        private bool IsInFreeSpace(int currentX, int currentY, int endX, int endY)
        {
            Vector2Int startSquare = new Vector2Int(currentX / SquareSize, currentY / SquareSize);
            Vector2Int endSquare = new Vector2Int(endX / SquareSize, endX / SquareSize);
            if (startSquare.Equals(endSquare) && FreeAreas.Contains(startSquare))
            {
                //Debug.Log($"{currentX},{currentY} and {endX},{endY} are on square {startSquare.x},{startSquare.y} and entirely in continuous free space");
                return true;
            }
            else
            {
                //Debug.Log($"{currentX},{currentY} and {endX},{endY} are on square {startSquare.x},{startSquare.y} and NOT in free space");
            }
            return false;
        }

        private void InitializeMap()
        {
            float start = Time.realtimeSinceStartup;
            //Debug.Log($"Loading pathfinder map at {Scale}x");

            // initialize everything as open space
            _grid = new Grid(Width, Height, this);


            GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");

            for (int i = 0; i < obstacles.Length; i++)
            {
                GameObject obstacleObject = obstacles[i];
                Obstacle obstacle = obstacleObject.GetComponent<Obstacle>();
                CollisionAsteroid collisionAsteroid = obstacleObject.GetComponent<CollisionAsteroid>();

                //Debug.Log($"Found {obstacleObject.name}: {obstacle}");

                obstacle.Setup(Level, ObstacleCount++);

                AddObstacle(obstacle);
                

                ObstaclePoints[obstacle.Id] = GetObstaclePoints(obstacle);

                foreach (int[] point in ObstaclePoints[obstacle.Id])
                {
                    if (point[0] > 0 && point[0] < _grid.Width && point[1] > 0 && point[1] < _grid.Height)
                    {
                        //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                        _grid.Nodes[point[0]][point[1]].IsWalkable = false; // set to unwalkable space
                    }
                }



                //Debug.Log($"{obstacle.name} is located at {obstacle.transform.position} with a bounds of {bounds}, a width of {width} and a height of {height}");

            }
            //Utilities.Print2DArray(_grid.Nodes.Select((n) => n.Select((innerNode) => innerNode.IsWalkable).ToArray()).ToArray());
            NeedsToBeUpdated = false;

            GetMapFreeSpace();
            //BakeMap();

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"InitializeMap() took {end} ms to complete.");
        }

        /// <summary>
        /// adds the index to Obstacles to Update 
        /// </summary>
        /// <param name="id"></param>
        public void AddToUpdateList(int id)
        {
            ObstaclesToUpdate.Add(id);
            NeedsToBeUpdated = true;
            //Debug.Log($"Setting #{id} to be updated");
        }
        public void AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
            ObstaclePoints.Add(new int[][] { });
            if (obstacle.IsMobile && !HasMovingObstacles)
            {
                HasMovingObstacles = true;
            }
            NeedsToBeUpdated = true;
            //Debug.Log($"Adding {obstacle.Name} to Map");
        }

        /// <summary>
        /// Gets all the points on an obstacle in the game and converts them to an array of points in the pathfinding map
        /// </summary>
        /// <param name="obstacle"></param>
        public int[][] GetObstaclePoints(Obstacle obstacle)
        {
            Collider2D collider = obstacle.ProximityCollider;
            Vector2 position = obstacle.GetPosition();
            Vector2 bounds = collider.bounds.size;

            int width = Convert.ToInt32(bounds.x);
            int height = Convert.ToInt32(bounds.y);
            int startX = Convert.ToInt32(position.x - (width / 2));
            int startY = Convert.ToInt32(position.y + (height / 2));

            


            List<int[]> points = new List<int[]>();

            for (int x = startX; x < startX + width; x += _scale) // go across the bounds, left to right (increasing)
            {
                for (int y = startY; y > startY - height; y -= _scale)
                {
                    Vector2Int point = new Vector2Int(x, y);
                    if (collider.OverlapPoint(point))
                    {
                        Vector2Int converted = ConvertToMapCoordinates(point);
                        //Debug.Log($" Converted {point} on the Map to (scaled) {converted} on the PathfindingMap");


                        if (IsLeftEdgePoint(point, collider))
                        {
                            for (int i = 1; i <= _padding; i++)
                            {
                                points.Add(new int[] { converted.x - i, converted.y });
                            }
                        }
                        else if (IsRightEdgePoint(point, collider))
                        {
                            for (int i = 1; i <= _padding; i++)
                            {
                                points.Add(new int[] { converted.x + i, converted.y });
                            }
                        }

                        else if (IsTopEdgePoint(point, collider))
                        {
                            for (int i = 1; i <= _padding; i++)
                            {
                                points.Add(new int[] { converted.x, converted.y + i });
                            }
                        }
                        else if (IsBottomEdgePoint(point, collider))
                        {
                            for (int i = 1; i <= _padding; i++)
                            {
                                points.Add(new int[] { converted.x, converted.y - i });
                            }
                        }

                        points.Add(new int[] { converted.x, converted.y });
                    }
                }

            }

            return points.ToArray();
        }
        
        public bool IsLeftEdgePoint(Vector2Int point, Collider2D collider)
        {
            return !collider.OverlapPoint(new Vector2(point.x - 1, point.y));
        }
        public bool IsRightEdgePoint(Vector2Int point, Collider2D collider)
        {
            return !collider.OverlapPoint(new Vector2(point.x + 1, point.y));
        }
        public bool IsTopEdgePoint(Vector2Int point, Collider2D collider)
        {
            return !collider.OverlapPoint(new Vector2(point.x, point.y + 1));
        }
        public bool IsBottomEdgePoint(Vector2Int point, Collider2D collider)
        {
            return !collider.OverlapPoint(new Vector2(point.x, point.y - 1));
        }
        /// <summary>
        /// This gets called by ships when the map needs to be updated to perform proper pathfinding. 
        /// Scenario 1. A ship is about to move on a map with no moving obstacles. Due to an obstacle being destroyed, the map is out of date. The Pathfinder checks the list of Obstacles to Update and sees 
        /// Index 1. The pathfinder checks the list of Obstacles and sees that index 1 is null. Then it grabs the array of map indexes for index 1 and sets them to false. The area is passable and the map is updated.
        /// Index #1 is removed from the list of Obstacles to Update.
        /// 
        /// Scenario 2. A ship is about to move on a map with moving obstacles. The ship passes all moving obstacles within its range to the Pathfinder. The Pathfinder takes whichever obstacles need to be updated, 
        /// finds their new position and points, and updates the map.
        /// 
        /// Scenario 3. A ship is moving on a preexisting route when an obstacle comes within its range. It passes that obstacle to the Pathfinder and the Pathfinder updates that obstacle and the ship finds a new path.
        /// </summary>
        public void UpdateMap(List<CollisionAsteroid> collisionAsteroids)
        {
            float start = Time.realtimeSinceStartup;
            //Debug.Log($"Updating map");

            if (collisionAsteroids.Count > 0) // there are asteroids within range of the ship that asked for the map to be updated
            {
                collisionAsteroids.ForEach((asteroid) =>
                {
                    //Debug.Log($"Updating the position of {asteroid.Name} on the pathfinding map");
                    foreach (int[] point in ObstaclePoints[asteroid.Id])
                    {
                        if (point[0] > 0 && point[0] < _grid.Width && point[1] > 0 && point[1] < _grid.Height)
                        {
                            _grid.Nodes[point[0]][point[1]].IsWalkable = true; // set its old position to walkable space
                        }
                    }

                    if (asteroid != null)
                    {
                        // Get the new points
                        ObstaclePoints[asteroid.Id] = GetObstaclePoints(asteroid);
                        foreach (int[] point in ObstaclePoints[asteroid.Id])
                        {
                            if (point[0] > 0 && point[0] < _grid.Width && point[1] > 0 && point[1] < _grid.Height)
                            {
                                //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                                _grid.Nodes[point[0]][point[1]].IsWalkable = false; // set its new position to unwalkable space
                            }
                        }
                    }
                    
                });
            }
            else // the ship sent an empty list which means there were no mobile obstacles within range but we should still update the map if need be for static obstacles
            {
                List<int> toRemove = new List<int>(); // contains indexes of ObstaclesToUpdate that have been updated and can be removed from the list

                for (int i = 0; i < ObstaclesToUpdate.Count; i++)
                {
                    int obstacleIndex = ObstaclesToUpdate[i];

                    Obstacle obstacle = Obstacles[obstacleIndex];

                    if (obstacle == null) // obstacle is dead
                    {
                        foreach (int[] point in ObstaclePoints[obstacleIndex])
                        {
                            _grid.Nodes[point[0]][point[1]].IsWalkable = true; // set its old position to walkable space
                        }

                        toRemove.Add(obstacleIndex);
                    }
                }
                toRemove.ForEach((obstacleIndex) =>
                {
                    ObstaclesToUpdate.Remove(obstacleIndex);
                });

                if (ObstaclesToUpdate.Count == 0)
                {
                    NeedsToBeUpdated = false; 
                }
            }


            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"UpdateMap() took {end} ms to complete.");

        }

        private int CalculateDistance(MapNode a, MapNode b)
        {
            int xDistance = Mathf.Abs(a.x - b.x);
            int yDistance = Mathf.Abs(a.y - b.y);
            int remaining = Mathf.Abs(xDistance - yDistance);
            return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * remaining;
        }

        private MapNode GetCheapestNode(MapNode[] array)
        {

            MergeSort(array, 0, array.Length - 1);
            //Debug.Log($"After sorting, the first node is {array.First().TotalCost}");
            return array.First();
        }

        private MapNode GetCheapestNode(List<MapNode> list)
        {
            MapNode cheapest = list.First();
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].TotalCost < cheapest.TotalCost)
                {
                    cheapest = list[i];
                }
            }
            return cheapest;
        }

        private void MergeSort(MapNode[] array, int start, int end)
        {
            // base case
            if (start < end)
            {
                // find the middle point
                int middle = (start + end) / 2;

                MergeSort(array, start, middle); // sort first half
                MergeSort(array, middle + 1, end);  // sort second half

                // merge the sorted halves
                MergeList(array, start, middle, end);
            }
        }

        private void MergeList(MapNode[] array, int start, int middle, int end)
        {
            MapNode[] leftArray = new MapNode[middle - start + 1];
            MapNode[] rightArray = new MapNode[end - middle];

            // fill in left array
            for (int i = 0; i < leftArray.Length; ++i)
                leftArray[i] = array[start + i];

            // fill in right array
            for (int i = 0; i < rightArray.Length; ++i)
                rightArray[i] = array[middle + 1 + i];

            /* Merge the temp arrays */

            // initial indexes of first and second subarrays
            int leftIndex = 0, rightIndex = 0;

            // the index we will start at when adding the subarrays back into the main array
            int currentIndex = start;

            // compare each index of the subarrays adding the lowest value to the currentIndex
            while (leftIndex < leftArray.Length && rightIndex < rightArray.Length)
            {
                if (leftArray[leftIndex].TotalCost <= rightArray[rightIndex].TotalCost)
                {
                    array[currentIndex] = leftArray[leftIndex];
                    leftIndex++;
                }
                else
                {
                    array[currentIndex] = rightArray[rightIndex];
                    rightIndex++;
                }
                currentIndex++;
            }

            // copy remaining elements of leftArray[] if any
            while (leftIndex < leftArray.Length) array[currentIndex++] = leftArray[leftIndex++];

            // copy remaining elements of rightArray[] if any
            while (rightIndex < rightArray.Length) array[currentIndex++] = rightArray[rightIndex++];
        }

        private void MakeDestinationList(MapNode endNode, Path path)
        {
            float start = Time.realtimeSinceStartup;
            List<Vector2> destinationList = new List<Vector2> {endNode.Vector};
            MapNode currentNode = endNode;
            while (currentNode.PreviousNode != null && Time.realtimeSinceStartup - start < TimeLimit)
            {
                destinationList.Add(currentNode.PreviousNode.Vector);
                currentNode = currentNode.PreviousNode;
            }
            if (Time.realtimeSinceStartup - start > TimeLimit)
            {
                Debug.Log($"Ran out of time while trying to make the path");
            }
            destinationList.Reverse();
            path.SetPoints(destinationList);
        }

        private MapNode startNode, endNode, currentNode;
        private Path path, cachedPath;

        public Path FindPath(int startX, int startY, int endX, int endY, bool isBaking = false)
        {
            //Debug.Log($"Trying to find a path from ({startX}, {startY}) to ({endX}, {endY})");
            float start = Time.realtimeSinceStartup;
            startNode = _grid.Nodes[startX][startY];
            endNode = _grid.Nodes[endX][endY];
            path = new Path(startX, startY, endX, endY);
            cachedPath = PathCache.FirstOrDefault((p) => path.Equals(p));

            //Debug.Log($"Cached path: {cachedPath}, contains: {PathCache.Contains(path)} path Id: {path.Id}");

            if (cachedPath != null)
            {
                //Debug.Log("Found a cached path!");
                cachedPath.IsCached = true;
                return cachedPath;
            }

            if (!endNode.IsWalkable)
            {
                if (!isBaking)
                {
                    Debug.Log($"The destination ({endNode.Vector}) isn't walkable space");
                }
                return null;
            }
            UncheckedNodes = new List<MapNode>() { startNode };
            CheckedNodes.Clear();

            for (int x = 0; x < _grid.Width; x++)
            {
                for (int y = 0; y < _grid.Height; y++)
                {
                    MapNode node = _grid.Nodes[x][y];
                    node.CostToHere = int.MaxValue;
                    node.TotalCost = int.MaxValue;
                    node.PreviousNode = null;
                }
            }

            startNode.CostToHere = 0;
            startNode.HueristicCost = CalculateDistance(startNode, endNode);
            startNode.CalculateTotalCost();

            int loops = 0;
            while (UncheckedNodes.Count > 0 && Time.realtimeSinceStartup - start < TimeLimit)
            {
                loops++;
                currentNode = GetCheapestNode(UncheckedNodes);

                if (loops % 20 == 0 && (CalculateDistance(currentNode, endNode) > 40 && !Utilities.HasObstaclesCloseToInTheWay(currentNode.Vector, endNode.Vector) ||
                    (IsInFreeSpace(currentNode.x, currentNode.y, endNode.x, endNode.y))))
                {
                    endNode.PreviousNode = currentNode;
                    MakeDestinationList(endNode, path);
                    PathCache.Add(path);
                    return path;
                }


                if (endNode == currentNode)
                {
                    MakeDestinationList(endNode, path);
                    PathCache.Add(path);
                    return path;
                }

                CheckedNodes.Add(currentNode);
                UncheckedNodes.Remove(currentNode);

                foreach (MapNode neighbor in GetNeighbors(currentNode))
                {
                    if (CheckedNodes.Contains(neighbor))
                    {
                        continue;
                    }
                    if (!neighbor.IsWalkable)
                    {
                        //Debug.Log($"Found an unwalkable node at {neighbor.Vector}");
                        CheckedNodes.Add(neighbor);
                        continue;
                    }

                    int tempCostToHere = currentNode.CostToHere + CalculateDistance(currentNode, neighbor);
                    if (tempCostToHere < neighbor.CostToHere)
                    {
                        neighbor.PreviousNode = currentNode;
                        neighbor.CostToHere = tempCostToHere;
                        neighbor.HueristicCost = CalculateDistance(neighbor, endNode);
                        neighbor.CalculateTotalCost();

                        if (!UncheckedNodes.Contains(neighbor))
                        {
                            //Debug.Log($"Unchecked Added {neighbor.Id}");
                            UncheckedNodes.Add(neighbor);
                            
                        }
                    }
                }
            }
            if (Time.realtimeSinceStartup - start > TimeLimit)
            {
                Debug.Log($"Ran out of time while trying to find a path from {startNode.Vector} to {endNode.Vector}");
            }

            // couldn't find the path
            return null;

        }

        private List<MapNode> GetNeighbors(MapNode node)
        {
            List<MapNode> neighbors = new List<MapNode>();

            if (node.x - 1 > 0) // There is space to the left
            {
                
                neighbors.Add(_grid.Nodes[node.x - 1][node.y]); // get left neighbor

                if (node.y - 1 >= 0)
                {
                    neighbors.Add(_grid.Nodes[node.x - 1][node.y - 1]); // get bottom left neighbor
                }

                if (node.y + 1 < _grid.Height)
                {
                    neighbors.Add(_grid.Nodes[node.x - 1][node.y + 1]); // get top left neighbor
                }

            }
            if (node.x + 1 < _grid.Width) // There is space to the right
            {

                neighbors.Add(_grid.Nodes[node.x + 1][node.y]); // get right neighbor

                if (node.y - 1 >= 0)
                {
                    neighbors.Add(_grid.Nodes[node.x + 1][node.y - 1]); // get bottom right neighbor
                }

                if (node.y + 1 < _grid.Height)
                {
                    neighbors.Add(_grid.Nodes[node.x + 1][node.y + 1]); // get top right neighbor
                }

            }

            if (node.y - 1 > 0) // there is space below
            {
                neighbors.Add(_grid.Nodes[node.x][node.y - 1]);
            }

            if (node.y + 1 < _grid.Height) // there is space above
            {
                neighbors.Add(_grid.Nodes[node.x][node.y + 1]);
            }

            return neighbors;
        }


        // Utility methods
        public bool IsObstacleAtPoint(Vector2 point)
        {
            return Obstacles.Any((obstacle) => obstacle != null && obstacle.Collider.OverlapPoint(point));
        }
        public Obstacle GetObstacleAtPoint(Vector2 point)
        {
            return Obstacles.Find((obstacle) => obstacle != null && obstacle.Collider.OverlapPoint(point));
        }
        public Vector2Int ConvertToMapCoordinates(Vector2 coords)
        {
            return new Vector2Int(Convert.ToInt32(Level.MapWidth - (Level.HalfMapWidth - coords.x)), Convert.ToInt32(Level.MapHeight - (Level.HalfMapHeight + coords.y))) / _scale;
        }
        public Vector2 ConvertToLevelCoordinates(Vector2Int coords)
        {
            return ConvertToLevelCoordinates(coords.x, coords.y);
        }
        public Vector2 ConvertToLevelCoordinates(int x, int y)
        {
            return new Vector2(-HalfWidth + x, HalfHeight - y) * _scale;
        }

        // [alert] only works for rectanglular maps
        /// <summary>
        /// A two-dimensional array of map nodes
        /// </summary>
        public class Grid {

            public int Width;
            public int Height;
            public MapNode[][] Nodes;
            public Pathfinder Pathfinder;
            public Grid(int width, int height, Pathfinder pathfinder)
            {
                Width = width;
                Height = height;
                Pathfinder = pathfinder;
                Nodes = new MapNode[width][];


                for (int x = 0; x < Width; x++)
                {
                    Nodes[x] = new MapNode[Height];
                    for (int y = 0; y < Height; y++)
                    {
                        Nodes[x][y] = new MapNode(x, y, this);
                    }
                }
            }

          
        }

        public class MapNode : IComparable<MapNode>
        {
            public int CostToHere; // The g cost
            public int HueristicCost; // The h cost
            public int TotalCost; // The f cost
            public int x, y;
            public readonly int Id;
            public bool IsWalkable;

            public MapNode PreviousNode;
            public Grid Grid;

            public Vector2 Vector;

            public MapNode(int x, int y, Grid grid)
            {
                this.x = x;
                this.y = y;
                Grid = grid;
                IsWalkable = true;
                Vector = Grid.Pathfinder.ConvertToLevelCoordinates(x, y);
                Id = Convert.ToInt32($"{x}{y}");
            }

            public void CalculateTotalCost()
            {
                TotalCost = CostToHere + HueristicCost;
            }
            public override bool Equals(System.Object obj)
            {
                // If parameter is null return false.
                if (obj == null)
                {
                    return false;
                }

                // If parameter cannot be cast to Point return false.
                MapNode p = obj as MapNode;
                if (p == null)
                {
                    return false;
                }

                // Return true if the fields match:
                return Id == p.Id;
            }
            public static bool operator ==(MapNode a, MapNode b)
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
                if ((MapNode)other == null)
                {
                    return -1;
                }
                return TotalCost - ((MapNode)other).TotalCost;
            }

            public int CompareTo(MapNode other)
            {
                return TotalCost - other.TotalCost;
            }
        }

        public class MapNodeComparer : IComparer<MapNode>
        {
            public int Compare(MapNode x, MapNode y)
            {
                return x.TotalCost.CompareTo(y.TotalCost);
            }
        }

        public class Path
        {
            public List<Vector2> Points;
            public int StartX, StartY, EndX, EndY;
            public int Id;
            public bool IsCached;

            public Path(int startX, int startY, int endX, int endY)
            {
                StartX = startX;
                StartY = startY;
                EndX = endX;
                EndY = endY;
                Id = Convert.ToInt32($"{startX}{startY}{endX}{endY}");

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
                return Id;
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
        }

    }
}

