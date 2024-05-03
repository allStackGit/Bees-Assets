using Assets.Scripts.Entities;
using Assets.Scripts.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public double TimeLimit = 5;
        public int DebugLoops = 100;
        public int MaxLoopsPerFrame = 1000;

        private Grid _grid;

        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly
        /// </summary>

        private int _scale = 1;
        public int Width, Height, HalfWidth, HalfHeight;
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
            Width = (int)Math.Ceiling((double)Level.MapWidth / _scale);
            Height = (int)Math.Ceiling((double)Level.MapHeight / _scale);
            HalfWidth = (int)Math.Ceiling((double)Level.HalfMapWidth / _scale);
            HalfHeight = (int)Math.Ceiling((double)Level.HalfMapHeight / _scale);

            //Level.StartCoroutine(InitializeMap());
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

                            //FindPath(startX, startY, endX, endY);

                        }

                    }

                }

            }

        }
        private int SquareSize = 100;
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

                            //if (!_grid.Nodes[x][y].IsWalkable)

                            //{

                            //    //Debug.Log($"Found unwalkable space at {x}, {y}, moving onto next square");

                            //    isFree = false;

                            //}

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

        public IEnumerator CalculateClearance()
        {

            float start = Time.realtimeSinceStartup;
            int totalLoopCount = 0;
            int minY, minX, maxY, maxX, boundsX, boundsY, largestNodeSize = 0;

            bool hasHitObstacle;
            MapNode currentNode;
            MapNode loopNode;

            HashSet<MapNode> checkedNodes = new HashSet<MapNode>(_grid.NodeSet.Where((n) => n.IsPermanant));
            //Queue<MapNode> uncheckedNodes = new Queue<MapNode>();
            HashSet<MapNode> borderNodes = new HashSet<MapNode>();
            HashSet<MapNode> potentialChildren= new HashSet<MapNode>();
            List<MapNode> potentialNodes;
            List<MapNode> largestNodes;
            List<MapNode> intersectingNodes = new List<MapNode>();
            //uncheckedNodes.Enqueue(_grid.GetNode(0, 0));
            int fullLoops = 0;
            int totalLargeNodes = 0;
            int mostChildrenCount = 0;
            while (checkedNodes.Count < _grid.NodeSet.Count)
            {
                float loopStart = Time.realtimeSinceStartup;
                fullLoops++;
                for (int y = 0; y < _grid.Height; y++)
                {
                    for (int x = 0; x < _grid.Width; x++)
                    {
                        currentNode = _grid.Nodes[x][y];

                        if (!checkedNodes.Contains(currentNode)) // skip obstacles and permanant nodes
                        {
                            hasHitObstacle = false;
                            minY = currentNode.y - currentNode.Clearance;
                            minX = currentNode.x - currentNode.Clearance;
                            maxY = currentNode.y + currentNode.Clearance;
                            maxX = currentNode.x + currentNode.Clearance;
                            potentialChildren.Clear();
                            while (!hasHitObstacle && maxX < _grid.Width && maxY < _grid.Height && minX >= 0 && minY >= 0)
                            {
                                borderNodes.Clear();
                                // bottom border
                                //Debug.Log($"Checking clearance ({currentNode.Clearance+1}) for {currentNode.Index}: minX: {minX}, maxX: {maxX}, minY: {minY}, maxY: {maxY}");
                                for (boundsX = minX; boundsX <= maxX; boundsX++)
                                {
                                    totalLoopCount++;
                                    //loopNode = _grid.GetNode(boundsX, maxY);
                                    loopNode = _grid.Nodes[boundsX][maxY];

                                    //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                    if (loopNode.IsPermanant)
                                    {
                                        //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                        hasHitObstacle = true;
                                        break;
                                    }
                                    //Debug.Log($"{loopNode.Index} is being added as a child of {currentNode}");
                                    borderNodes.Add(loopNode);
                                }

                                // top border
                                if (!hasHitObstacle)
                                {
                                    for (boundsX = minX; boundsX <= maxX; boundsX++)
                                    {
                                        totalLoopCount++;
                                        //loopNode = _grid.GetNode(boundsX, minY);
                                        loopNode = _grid.Nodes[boundsX][minY];
                                        //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                        if (loopNode.IsPermanant)
                                        {
                                            //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                            hasHitObstacle = true;
                                            break;
                                        }

                                        borderNodes.Add(loopNode);
                                    }

                                    // right border
                                    if (!hasHitObstacle)
                                    {
                                        for (boundsY = maxY - 1; boundsY > minY; boundsY--)
                                        {
                                            totalLoopCount++;
                                            //loopNode = _grid.GetNode(maxX, boundsY);
                                            loopNode = _grid.Nodes[maxX][boundsY];
                                            //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                            if (loopNode.IsPermanant)
                                            {
                                                hasHitObstacle = true;
                                                //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                                break;
                                            }
                                            borderNodes.Add(loopNode);
                                        }

                                        // left border
                                        if (!hasHitObstacle)
                                        {
                                            for (boundsY = maxY - 1; boundsY > minY; boundsY--)
                                            {
                                                totalLoopCount++;
                                                //loopNode = _grid.GetNode(minX, boundsY);
                                                loopNode = _grid.Nodes[minX][boundsY];
                                                //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                                if (loopNode.IsPermanant)
                                                {
                                                    hasHitObstacle = true;
                                                    //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                                    break;
                                                }
                                                borderNodes.Add(loopNode);
                                            }

                                            if (!hasHitObstacle)
                                            {
                                                potentialChildren.UnionWith(borderNodes);
                                                currentNode.Clearance++;
                                                maxY++;
                                                maxX++;
                                                minY--;
                                                minX--;
                                            }
                                        }
                                    }
                                }
                            }
                            currentNode.OriginalClearance = currentNode.Clearance;
                            currentNode.Children.UnionWith(potentialChildren);
                            //PreCheckedNodes.Remove(currentNode);
                            checkedNodes.Add(currentNode);
                            //Debug.Log($"Completed {currentNode}");

                            float loopTime = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
                            if (loopTime % 1000 < 100)
                            {
                                //Debug.Log($"Completed node after {loopTime} ms. Loops: {totalLoopCount} / {fullLoops}, Checked: {checkedNodes.Count} / {_grid.NodeSet.Count}");
                                //yield break;
                                yield return ConfigData.WaitForEndOfFrame;
                            }

                        }
                    }
                }
                potentialNodes = _grid.NodeSet.Where((n) => !n.IsPermanant && n.Clearance > 1 && n.ContainerNode == n).ToList();
                if (potentialNodes.Count > 0)
                {
                    largestNodeSize = potentialNodes.OrderByDescending(n => n.Clearance).First().Clearance;
                    largestNodes = potentialNodes.Where((n) => n.Clearance == largestNodeSize).ToList();
                    //largestNodes = potentialNodes.OrderByDescending(n => n.Clearance).ToList();

                    while (largestNodes.Count > 0)
                    {
                        loopNode = largestNodes[0];

                        // check for intersecting nodes
                        int oldCount = largestNodes.Count;
                        largestNodes.RemoveAll((n) => 
                        {
                            if (n == loopNode)
                            {
                                return false;
                            }
                            foreach (MapNode child in n.Children)
                            {
                                if (loopNode.Children.Contains(child))
                                {
                                    return true;
                                }
                            }
                            return false;
                        });

                        totalLargeNodes++;
                        loopNode.Children.ToList().ForEach((child) =>
                        {
                            child.IsPermanant = true;
                            child.ContainerNode = loopNode;
                            child.Children.Clear();
                            child.Clearance = 1;
                        });
                        loopNode.IsPermanant = true;
                        _grid.ClearanceMap.Add(loopNode);
                        largestNodes.Remove(loopNode);
                        //Debug.Log($"The largest node (tied) is {loopNode}");
                        yield return ConfigData.WaitForEndOfFrame;
                    }
                    // reset other nodes
                    checkedNodes.ToList().ForEach((node) =>
                    {
                        if (!node.IsPermanant && node.Clearance > 0)
                        {
                            node.Clearance = 1;
                            node.ContainerNode = node;
                            node.Children.Clear();
                        }

                    });
                    //_grid.NodeSet.First().GetNeighbors().ForEach((n) => uncheckedNodes.Enqueue(_grid.GetNode(n.x, n.y)));
                    float loopTime = (Time.realtimeSinceStartup - loopStart) * 1000; // seconds to milliseconds
                    Debug.Log($" #{fullLoops} took {loopTime}ms largest nodes so far: {totalLargeNodes}");
                    //mostChildrenCount /= 2; // [alert] this could be a reason for children to not get counted


                    //_grid.PrintPermanantNodesImage();
                    checkedNodes = _grid.NodeSet.Where((n) => n.IsPermanant).ToHashSet();
                    yield return ConfigData.WaitForEndOfFrame;
                }
                else
                {
                    Debug.Log($"No more potential nodes: {totalLargeNodes}");

                    // mark the rest of the nodes as permanant
                    checkedNodes.ToList().ForEach((node) =>
                    {
                        if (!node.IsPermanant && node.Clearance == 1)
                        {
                            node.IsPermanant = true;
                            node.ContainerNode = node;
                            totalLargeNodes++;
                        }

                    });
                }
            }  
            _grid.PrintPermanantNodesImage();


            Debug.Log($"Checked Nodes: {checkedNodes.Count}");
            Debug.Log($"Total nodes: {_grid.NodeSet.Count} / {totalLargeNodes}");
            HashSet<MapNode> missingNodes = new HashSet<MapNode>(_grid.NodeSet);
            missingNodes.ExceptWith(checkedNodes);
            if (missingNodes.Count > 0)
            {
                Debug.Log($"Missing nodes: {_grid.NodeSet.Count}, {_grid.NodeSet.First()}");
            }

            //ghostNode.DebugNodeImage();
            //_grid.PrintGridImage();


            AssembleClearanceMap();
            SaveClearanceMap();
            //LoadClearanceMap();
            //_grid.PrintPermanantNodesImage();
            //SaveClearanceMap("_b");

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"InitializeMap() took {end} ms to complete. There were {totalLoopCount} loops measuring clearance");
        }

        public void AssembleClearanceMap()
        {
            _grid.ClearanceMap.ToList().ForEach((node) =>
            {
                //node.DebugNodeImage();
                node.GetNeighbors();
                //Debug.Log($"Assembled {node}");
            });

        }

        public void SaveClearanceMap(string version = "")
        {
            string json = "[";            
            _grid.ClearanceMap.ToList().ForEach((node) => json += $"{node.ToJson()}, ");
            json = json.Remove(json.Length - 2);
            json += "]";
            string path = $"{ConfigData.GetBasePath()}/ClearanceMap{version}.json";
            File.WriteAllText(path, json);
        }
        public IEnumerator LoadClearanceMap()
        {
            float start = Time.realtimeSinceStartup;
            string contents = "";
            StreamReader fileStream = new StreamReader($"{ConfigData.GetBasePath()}/ClearanceMap.json");
            int loops = 0;

            while (!fileStream.EndOfStream)
            {
                loops++;
                string line = fileStream.ReadLine();
                contents += line;
                if (loops % 1000 == 0)
                {
                    yield return ConfigData.WaitForEndOfFrame;
                }
            }

            fileStream.Close();
            List<dynamic> nodes = Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(contents));

            foreach (dynamic node in nodes)
            {
                loops++;
                MapNode mapNode = _grid.Nodes[(int)node.x][(int)node.y];
                //mapNode.ContainerNode = _grid.Nodes[(int)node.CN.x][(int)node.CN.y];
                mapNode.ContainerNode = mapNode;
                mapNode.Clearance = (int)node.OC;
                mapNode.OriginalClearance = mapNode.Clearance;

                List<dynamic> children = Utilities.JArrayToList<dynamic>(node.C);
                List<dynamic> neighbors = Utilities.JArrayToList<dynamic>(node.N);

                children.ForEach((child) =>
                {
                    MapNode mapChild = _grid.Nodes[(int)child.x][(int)child.y];
                    mapChild.ContainerNode = mapNode;
                    mapChild.IsPermanant = true;
                    mapNode.Children.Add(mapChild);
                });

                neighbors.ForEach((neighbor) =>
                {
                    MapNode mapNeighbor = _grid.Nodes[(int)neighbor.x][(int)neighbor.y];
                    mapNeighbor.IsPermanant = true;
                    mapNode.Neighbors.Add(mapNeighbor);
                });
                mapNode.IsPermanant = true;
                _grid.ClearanceMap.Add(mapNode);
                //Debug.Log($"Loaded {mapNode}");
                if (loops % 100 == 0)
                {
                    yield return ConfigData.WaitForEndOfFrame;
                }
            }
            _grid.PrintPermanantNodesImage();
            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"Loaded clearance map in {end} ms");
        }
        public void InitializeMap()
        {
            //Debug.Log($"Loading pathfinder map at {Scale}x");
            // initialize everything as open space
            _grid = new Grid(Width, Height, this);

            GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");
            GameState state = Level.GetState();

            for (int i = 0; i < obstacles.Length; i++)
            {

                GameObject obstacleObject = obstacles[i];
                Obstacle obstacle = obstacleObject.GetComponent<Obstacle>();
                //Debug.Log($"Found {obstacleObject.name}: {obstacle}");

                obstacle.Setup(Level, state.GetId());
                AddObstacle(obstacle);
                ObstaclePoints[obstacle.Id] = GetObstaclePoints(obstacle);

                //Debug.Log($"The first point on {obstacle.Name} is ({ObstaclePoints[obstacle.Id][0][0]}, {ObstaclePoints[obstacle.Id][0][1]}) on the map");

                foreach (int[] point in ObstaclePoints[obstacle.Id])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                        _grid.Nodes[point[0]][point[1]].Clearance = 0; // set to unwalkable space
                        _grid.Nodes[point[0]][point[1]].OriginalClearance = 0;
                        _grid.Nodes[point[0]][point[1]].IsPermanant = true;
                    }
                    else if (!obstacle.IsMapBorder)
                    {
                        Debug.Log($"Invalid indexes: {point[0]}, {point[1]}");
                    }
                }
            }

            Level.StartCoroutine(LoadClearanceMap());
            //Level.StartCoroutine(CalculateClearance());

            _grid.ClearanceMapList = _grid.ClearanceMap.ToList();
            NeedsToBeUpdated = false;

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
            if (collider == null)
            {
                collider = obstacle.Collider;
            }

            Vector2 position = obstacle.GetPosition();
            Vector2 bounds = collider.bounds.size;



            int width = (int)Math.Ceiling(bounds.x);
            int height = (int)Math.Ceiling(bounds.y);
            int startX = (int)Math.Floor(position.x - (width / 2));
            int startY = (int)Math.Floor(position.y + (height / 2));

            //Debug.Log($"Checking points on {obstacle.Name} starting at {startX} and going across {width}");

            List<int[]> points = new List<int[]>();

            for (int x = startX; x < startX + width; x += _scale) // go across the bounds, left to right (increasing)
            {
                for (int y = startY; y > startY - height; y -= _scale)
                {
                    Vector2Int point = new Vector2Int(x, y);
                    if (collider.OverlapPoint(point))
                    {
                        Vector2Int converted = ConvertToMapCoordinates(point);
                        points.Add(new int[] { converted.x, converted.y });
                    }
                    else
                    {
                        Debug.Log($"{point} is in the bounds of {obstacle.Name} but does not overlap the collider");
                    }
                }
            }
            return points.ToArray();
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
                        if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                        {
                            _grid.Nodes[point[0]][point[1]].Clearance = _grid.Nodes[point[0]][point[1]].OriginalClearance; // set its old position to the original clearance
                        }
                    }

                    if (asteroid != null)
                    {
                        // Get the new points
                        ObstaclePoints[asteroid.Id] = GetObstaclePoints(asteroid);
                        foreach (int[] point in ObstaclePoints[asteroid.Id])
                        {
                            if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                            {
                                //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                                _grid.Nodes[point[0]][point[1]].Clearance = 0; // set its new position to unwalkable space
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
                            try
                            {
                                _grid.Nodes[point[0]][point[1]].Clearance = _grid.Nodes[point[0]][point[1]].OriginalClearance; // set its old position to the original clearance
                            }
                            catch (Exception e)
                            {
                                Debug.Log($"Had an error with index points {point[0]}, {point[1]} on _grid.Nodes {_grid.Nodes}");
                                throw e;
                            }
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
            //int xDistance = Mathf.Abs(a.x - b.x);
            //int yDistance = Mathf.Abs(a.y - b.y);
            //int remaining = Mathf.Abs(xDistance - yDistance);
            //return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * remaining;

            return (int) Vector2.Distance(a.Vector, b.Vector);

        }
        private MapNode GetCheapestNode(List<MapNode> list)
        {
            MapNode cheapest = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].TotalCost < cheapest.TotalCost)
                {
                    cheapest = list[i];
                }
            }
            return cheapest;
        }
        private void MakeDestinationList(MapNode endNode, Path path)
        {
            float start = Time.realtimeSinceStartup;
            List<Vector2> destinationList = new List<Vector2> { endNode.Vector };
            MapNode currentNode = endNode;
            while (currentNode.PreviousNode != null && Time.realtimeSinceStartup - start < TimeLimit)
            {
                destinationList.Add(currentNode.PreviousNode.Vector);
                currentNode.PreviousNode.IsPartOfPath = true;
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
        public MapNode FindNearestWalkablePoint(MapNode startNode, MapNode endNode, int minimumClearance)
        {

            int loops = 0;
            int baseInterval = 1;
            int yInterval = -1; // N
            int xInterval = 1; // E

            /*
            Find the direction from start node to end node (N, NE, E, SE, S, SW, W, NW)
            Find the first walkable point in that same direction
            Find the first walkable point in the opposite direction (from end to start)
            Return the point closest to the original end point
             */


            if (startNode.x < endNode.x && startNode.y < endNode.y)
            {
                //direction = 2; // SE
                xInterval = 1 * baseInterval;
            }
            else if (startNode.x > endNode.x && startNode.y < endNode.y)
            {
                //direction = 4; // SW
                yInterval = 1 * baseInterval;
                xInterval = -1 * baseInterval;
            }
            else if (startNode.x > endNode.x && startNode.y > endNode.y)
            {
                //direction = 6; // NW
                yInterval = -1 * baseInterval;
                xInterval = -1 * baseInterval;
            }
            else if (startNode.x == endNode.x && startNode.y > endNode.y)
            {
                //direction = 7; // N
                yInterval = -1 * baseInterval;
                xInterval = 0;
            }
            else if (startNode.x < endNode.x && startNode.y == endNode.y)
            {
                //direction = 1; // E
                yInterval = 0;
                xInterval = 1 * baseInterval;
            }
            else if (startNode.x == endNode.x && startNode.y < endNode.y)
            {
                //direction = 3; // S
                yInterval = 1 * baseInterval;
                xInterval = 0;
            }
            else if (startNode.x > endNode.x && startNode.y == endNode.y)
            {
                //direction = 5; // W
                yInterval = 0;
                xInterval = -1 * baseInterval;
            }

            //oppositeDirection = (direction + 4) % 8;

            //Debug.Log($"startNode: {startNode.Vector}, endNode: {endNode.Vector} index direction: {direction}, opposite index direction: {oppositeDirection}");
            MapNode directionNode = _grid.Nodes[endNode.x + xInterval][endNode.y + yInterval];
            MapNode oppositeDirectionNode = _grid.Nodes[endNode.x - xInterval][endNode.y - yInterval];

            while (loops < 100)
            {
                loops++;
                //Debug.Log($"Moving in directions {direction}, and {oppositeDirection} and checking points {directionNode.Vector} and {oppositeDirectionNode.Vector}");
                if (directionNode.Clearance >= minimumClearance)
                {
                    return directionNode;
                }

                if (oppositeDirectionNode.Clearance >= minimumClearance)
                {
                    return oppositeDirectionNode;
                }

                int yIncrease = directionNode.y + yInterval;
                int xIncrease = directionNode.x + xInterval;
                int yDecrease = oppositeDirectionNode.y - yInterval;
                int xDecrease = oppositeDirectionNode.x - xInterval;
                if (yIncrease > _grid.Height - 1)
                {
                    yIncrease = _grid.Height - 1;
                }
                else if (yIncrease < 0)
                {
                    yIncrease = 0;
                }
                if (xIncrease > _grid.Width - 1)
                {
                    xIncrease = _grid.Width - 1;
                }
                else if (xIncrease < 0){
                    xIncrease = 0;
                }
                if (yDecrease < 0)
                {
                    yDecrease = 0;
                }
                else if (yDecrease > _grid.Height - 1)
                {
                    yDecrease = _grid.Height - 1;
                }
                if (xDecrease < 0)
                {
                    xDecrease = 0;
                }
                else if (xDecrease > _grid.Width - 1)
                {
                    xDecrease = _grid.Width - 1;
                }
                directionNode = _grid.Nodes[xIncrease][yIncrease];
                oppositeDirectionNode = _grid.Nodes[xDecrease][yDecrease];
            }
            if (loops == 100) // [debug]
            {
                Debug.LogError($"The loop broke after 100 loops trying to find a walkable point near {endNode.Vector} starting from {startNode.Vector}");
            }
            return null;

        }
        public IEnumerator FindPath(int startX, int startY, int endX, int endY, int minimumClearance, int maximumClearance, Action<Path> callback)
        {
            //minimumClearance = 1;
            //maximumClearance = 1;
            //Debug.Log($"Trying to find a path with clearance ({minimumClearance} - {maximumClearance}) from ({startX}, {startY}) to ({endX}, {endY})");
            float start = Time.realtimeSinceStartup;
            startNode = _grid.Nodes[startX][startY];
            endNode = _grid.Nodes[endX][endY];
            path = new Path(startX, startY, endX, endY);
            cachedPath = PathCache.FirstOrDefault((p) => path.Equals(p));

            //Debug.Log($"Cached path: {cachedPath}, contains: {PathCache.Contains(path)} path Id: {path.Id}");


            if (cachedPath != null)
            {
                Debug.Log("Found a cached path!");
                cachedPath.IsCached = true;
                callback(cachedPath);
                yield break;
            }

            if (endNode.ContainerNode.Clearance < maximumClearance)
            {

                //if (!isBaking)
                //{
                //    Debug.Log($"The destination ({endNode.Vector}) isn't walkable space");
                //    endNode = FindNearestWalkablePoint(startNode, endNode);
                //    Debug.Log($"Found new end point that is walkable: {endNode.Vector}");
                //}
                //else
                //{
                //    endNode = FindNearestWalkablePoint(startNode, endNode);
                //}


                Debug.Log($"The destination ({endNode.Vector}) isn't walkable space");
                endNode = FindNearestWalkablePoint(startNode, endNode, maximumClearance);
                Debug.Log($"Found new end point that is walkable: {endNode.Vector}");
            }



            if (startNode.ContainerNode.Clearance < maximumClearance)
            {
                Debug.Log($"The start ({startNode.Vector}) isn't walkable space");
                startNode = FindNearestWalkablePoint(endNode, startNode, maximumClearance);
                Debug.Log($"Found new start point that is walkable: {startNode.Vector}");
            }

            UncheckedNodes = new List<MapNode>() { startNode };
            CheckedNodes.Clear();

            //for (int x = 0; x < _grid.Width; x++)
            //{
            //    for (int y = 0; y < _grid.Height; y++)
            //    {
            //        MapNode node = _grid.Nodes[x][y];
            //        node.CostToHere = int.MaxValue;
            //        node.CalculateTotalCost();
            //        node.PreviousNode = null;
            //        node.HasBeenChecked = false;
            //        node.IsPartOfPath = false;
            //    }
            //}

            Queue<MapNode> queue = new Queue<MapNode>();
            HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
            queue.Enqueue(_grid.ClearanceMapList.First());

            while (queue.Count > 0)
            {
                MapNode node = queue.Dequeue();
                if (!checkedNodes.Contains(node))
                {
                    checkedNodes.Add(node);
                    node.CostToHere = int.MaxValue;
                    node.CalculateTotalCost();
                    node.PreviousNode = null;
                    node.HasBeenChecked = false;
                    node.IsPartOfPath = false;
                    foreach (MapNode neighbor in node.Neighbors)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
                
            }
            startNode.CostToHere = 0;
            startNode.HueristicCost = CalculateDistance(startNode, endNode);
            startNode.CalculateTotalCost();

            int loops = 0;
            Debug.Log($"Startnode: {startNode}");
            while (UncheckedNodes.Count > 0 && Time.realtimeSinceStartup - start < TimeLimit)
            {
                loops++;
                currentNode = GetCheapestNode(UncheckedNodes);

                // skips ahead to further down the line if it detects we're in free space
                //if (loops % 20 == 0 && (IsInFreeSpace(currentNode.x, currentNode.y, endNode.x, endNode.y)))
                //{
                //    endNode.PreviousNode = currentNode;
                //    MakeDestinationList(endNode, path);
                //    PathCache.Add(path);
                //    return path;
                //}

                //skip ahead to further down if we raycasted a straight line
                if (loops % 20 == 0 && (CalculateDistance(currentNode, endNode) > 40 && !Utilities.HasObstaclesInTheWay(currentNode.Vector, endNode.Vector)))
                {
                    endNode.PreviousNode = currentNode;
                    MakeDestinationList(endNode, path);
                    PathCache.Add(path);
                    Debug.Log($"Found a straight line from {currentNode.Vector} to the end {endNode.Vector}");
                    _grid.DebugGridAsImage(new Vector2Int(endNode.x, endNode.y));
                    callback(path);
                    yield break;
                }

                if (currentNode == endNode)
                {
                    Debug.Log($"Reached the end destination");
                    MakeDestinationList(endNode, path);
                    PathCache.Add(path);
                    _grid.DebugGridAsImage(new Vector2Int(currentNode.x, currentNode.y));
                    callback(path);
                    yield break;
                }
                else if (currentNode.Children.Contains(endNode))
                {
                    endNode.PreviousNode = currentNode;
                    MakeDestinationList(endNode, path);
                    PathCache.Add(path);
                    Debug.Log($"Found the end node as a child of another node {currentNode}");
                    _grid.DebugGridAsImage(new Vector2Int(endNode.x, endNode.y));
                    callback(path);
                    yield break;
                }
                UncheckedNodes.Remove(currentNode);
                currentNode.HasBeenChecked = true;
                CheckedNodes.Add(currentNode);

                //Debug.Log($"Getting neighbors for {currentNode}");
                foreach (MapNode neighbor in currentNode.ContainerNode.Neighbors)
                {
                    if (CheckedNodes.Contains(neighbor))
                    {
                        if (loops > DebugLoops)
                        {
                            Debug.Log($"Found an already checked node at {neighbor}");
                        }
                        continue;
                    }

                    if (neighbor.Clearance < maximumClearance)
                    {
                        if (loops > DebugLoops)
                        {
                            Debug.Log($"Found an unwalkable node at {neighbor}");
                        }
                        currentNode.HasBeenChecked = true;
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
                            if (loops > DebugLoops)
                            {
                                Debug.Log($"Unchecked Added {neighbor}");
                            }
                            UncheckedNodes.Add(neighbor);
                        }
                        else
                        {
                            if (loops > DebugLoops)
                            {
                                Debug.Log($"Unchecked nodes already contains {neighbor}");
                            }
                        }
                    }
                    else
                    {
                        if (loops > DebugLoops)
                        {
                            Debug.Log($"the cost to here {tempCostToHere} was >= to {neighbor.CostToHere} with {neighbor}");
                        }
                    }
                }

                if (loops % MaxLoopsPerFrame == 0)
                {
                    yield return ConfigData.WaitForEndOfFrame;
                }
            }

            if (Time.realtimeSinceStartup - start > TimeLimit)
            {
                Debug.Log($"Ran out of time while trying to find a path from {startNode.Vector} to {endNode.Vector}");
            }
            else if (UncheckedNodes.Count == 0)
            {
                Debug.Log($"No more nodes to check.  checkedNodes: {CheckedNodes.Count} / {_grid.TotalNodes} / {_grid.ClearanceMap.Count}  CurrentNode: {currentNode},");
            }

            // couldn't find the path
            _grid.DebugGridAsImage(new Vector2Int(currentNode.x, currentNode.y));
            callback(null);
            yield break;

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
            return new Vector2Int((int) Math.Round(Level.MapWidth - (Level.HalfMapWidth - coords.x)), (int)Math.Round(Level.MapHeight - (Level.HalfMapHeight + coords.y))) / _scale;
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
        public class Grid
        {
            public int Width;
            public int Height;
            public int TotalNodes;
            public HashSet<MapNode> NodeSet = new HashSet<MapNode>();
            public HashSet<MapNode> ClearanceMap = new HashSet<MapNode>();
            public List<MapNode> ClearanceMapList = new List<MapNode>();
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
            public MapNode GetNode(int x, int y)
            {
                //if (x == 487 && y == 66)
                //{
                //    Debug.Log($"Found ghost node! {Nodes[x][y]}");
                //}
                return Nodes[x][y];
            }
            public void DebugGridAsImage(Vector2Int lastNode)
            {
                Texture2D texture = new Texture2D(Width * 2, Height * 2, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                MapNode node;
                for (int y = 0; y < Height * 2; y += 2)
                {
                    for (int x = 0; x < Width * 2; x += 2)
                    {
                        Color color = Color.grey; // has not been checked
                        node = Nodes[x/2][y/2];
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
                            color = Color.black;
                        }
                        
                       
                        
                        texture.SetPixel(x, Height * 2 - (y + 1), color); // regular
                        texture.SetPixel(x + 1, Height * 2 - (y + 1), color); // right
                        texture.SetPixel(x, Height * 2 - y, color); // down
                        texture.SetPixel(x + 1, Height * 2 - y, color); // down and right
                    }
                }
                texture.SetPixel(lastNode.x, Height * 2 - (lastNode.y + 1), ConfigData.GetUIColor("medium"));
                texture.SetPixel(lastNode.x + 1, Height * 2 - (lastNode.y + 1), ConfigData.GetUIColor("medium"));
                texture.SetPixel(lastNode.x, Height * 2 - lastNode.y, ConfigData.GetUIColor("medium"));
                texture.SetPixel(lastNode.x + 1, Height * 2 - lastNode.y, ConfigData.GetUIColor("medium"));
                //Debug.Log($"Setting last pixel at ({lastNode.x}, {(Height - (lastNode.y + 1))}) to yellow");
                //Color[] pixels = texture.GetPixels();
                //System.Array.Reverse(pixels, 0, pixels.Length);
                //texture.SetPixels(pixels);
                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{Utilities.Hash()}.png";
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            public void PrintGridImage(int scale = 2)
            {
                Texture2D texture = new Texture2D(Width * scale, Height * scale, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
                bool hasPlacedSquare = false;
                MapNode node;
                for (int y = 0; y < Height * scale; y += scale)
                {
                    for (int x = 0; x < Width * scale; x += scale)
                    {
                        Color color = Color.yellow; // has not been checked
                        node = Nodes[x / scale][y / scale];
                        if (checkedNodes.Contains(node)) 
                        {
                            continue;
                        }
                        List<MapNode> children = node.Children.Where((c) => !checkedNodes.Contains(c)).ToList();
                        if (node.Clearance == 0) // obstacle
                        {
                            color = ConfigData.GetUIColor("bad");
                        }
                        else if (node.Clearance == 1) // not an obstacle, checked, and part of the path
                        {
                            //Debug.Log(node);
                            color = Color.black;
                        }
                        else if (node.Clearance > 1) // not an obstacle, checked, and not part of the path
                        {
                            color = Color.blue;
                            //color = new Color(Utilities.RandomInt(scale) + .5f / scale.0f, Utilities.RandomInt(scale) + .5f / scale.0f, Utilities.RandomInt(scale) + .5f / scale.0f);
                        }

                        if (!hasPlacedSquare && children.Count > 1)
                        {

                            int minX = children.OrderBy((c) => c.x).First().x;
                            int maxX = children.OrderByDescending((c) => c.x).First().x;
                            int minY = children.OrderBy((c) => c.y).First().y;
                            int maxY = children.OrderByDescending((c) => c.y).First().y;
                            Color borderColor = new Color(Utilities.RandomInt(2) + .5f / 2.0f, Utilities.RandomInt(2) + .5f / 2.0f, Utilities.RandomInt(2) + .5f / 2.0f);

                            children.ForEach((childNode) =>
                            {
                                if (childNode.x == minX || childNode.x == maxX || childNode.y ==  minY || childNode.y == maxY)
                                {
                                    color = borderColor;
                                }
                                else
                                {
                                    color = Color.blue;
                                }

                                checkedNodes.Add(childNode);

                                for (int v = 0; v < scale; v++)
                                {
                                    for (int h = 0; h < scale; h++)
                                    {
                                        texture.SetPixel((childNode.x * scale) + h, (Height * scale) - ((childNode.y * scale) + (1 - v)), color); // regular
                                    }
                                }
                                checkedNodes.Add(childNode);

                            });

                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), Color.red); // regular
                                }
                            }
                            //hasPlacedSquare = true;
                            checkedNodes.Add(node);

                        }
                        else
                        {
                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                                }
                            }

                        }


                    }
                }

                //for (int y = 0; y < Height * scale; y += scale)
                //{
                //    for (int x = 0; x < Width * scale; x += scale)
                //    {
                //        Color color = Color.yellow; // has not been checked
                //        node = Nodes[x / scale][y / scale];
                //        if (checkedNodes.Contains(node))
                //        {
                //            color = Color.blue;
                //        }
                //        else if (node.Clearance == 0) // obstacle
                //        {
                //            color = ConfigData.GetUIColor("bad");
                //        }
                //        else if (node.Clearance == 1) // not an obstacle, checked, and part of the path
                //        {
                //            //Debug.Log(node);
                //            color = Color.grey;
                //        }

                //        for (int v = 0; v < scale; v++)
                //        {
                //            for (int h = 0; h < scale; h++)
                //            {
                //                texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                //            }
                //        }

                //    }
                //}

                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{Utilities.Hash()}.png";
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            public void PrintPermanantNodesImage(int scale = 2)
            {
                Texture2D texture = new Texture2D(Width * scale, Height * scale, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
                bool hasPlacedSquare = false;
                MapNode node;
                for (int y = 0; y < Height * scale; y += scale)
                {
                    for (int x = 0; x < Width * scale; x += scale)
                    {
                        Color color = Color.red; // has not been checked
                        node = Nodes[x / scale][y / scale];
                        if (checkedNodes.Contains(node))
                        {
                            continue;
                        }
                        List<MapNode> children = node.Children.ToList();
                        if (node.Clearance == 0) // obstacle
                        {
                            color = ConfigData.GetUIColor("bad");
                        }
                        else if (node.Clearance == 1 && node.IsPermanant) // not an obstacle, checked, and part of the path
                        {
                            //Debug.Log(node);
                            color = Color.black;
                        }
                        //else if (node.Clearance > 1) // not an obstacle, checked, and not part of the path
                        //{
                        //    color = Color.blue;
                        //    //color = new Color(Utilities.RandomInt(scale) + .5f / scale.0f, Utilities.RandomInt(scale) + .5f / scale.0f, Utilities.RandomInt(scale) + .5f / scale.0f);
                        //}

                        if (!hasPlacedSquare && children.Count > 1 && node.IsPermanant)
                        {

                            int minX = children.OrderBy((c) => c.x).First().x;
                            int maxX = children.OrderByDescending((c) => c.x).First().x;
                            int minY = children.OrderBy((c) => c.y).First().y;
                            int maxY = children.OrderByDescending((c) => c.y).First().y;
                            Color borderColor = Color.yellow; //new Color(Utilities.RandomInt(2) + .5f / 2.0f, Utilities.RandomInt(2) + .5f / 2.0f, Utilities.RandomInt(2) + .5f / 2.0f);

                            children.ForEach((childNode) =>
                            {
                                if (childNode.x == minX || childNode.x == maxX || childNode.y == minY || childNode.y == maxY)
                                {
                                    color = borderColor;
                                }
                                else
                                {
                                    color = Color.cyan;
                                }

                                checkedNodes.Add(childNode);

                                for (int v = 0; v < scale; v++)
                                {
                                    for (int h = 0; h < scale; h++)
                                    {
                                        texture.SetPixel((childNode.x * scale) + h, (Height * scale) - ((childNode.y * scale) + (1 - v)), color); // regular
                                    }
                                }
                                checkedNodes.Add(childNode);

                            });

                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), Color.black); // regular
                                }
                            }
                            //hasPlacedSquare = true;
                            checkedNodes.Add(node);

                        }
                        else
                        {
                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                                }
                            }

                        }


                    }
                }

                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{Utilities.Hash()}.png";
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
        }


        public class MapNode : IComparable<MapNode>
        {

            public int CostToHere; // The g cost
            public int HueristicCost; // The h cost
            public int TotalCost; // The f cost
            /// <summary>
            /// The x and y indices of the map node in the grid
            /// </summary>
            public int x, y;
            public Vector2Int Index;
            public readonly int Id;
            public int OriginalClearance;
            public int Clearance;
            public bool HasBeenChecked;
            public bool IsPartOfPath;
            public bool IsPermanant;
            public MapNode PreviousNode;
            public MapNode ContainerNode;
            public HashSet<MapNode> Children = new HashSet<MapNode>();
            public HashSet<MapNode> Neighbors = new HashSet<MapNode>();
            public Grid Grid;

            /// <summary>
            /// The Level coordinates of the Node
            /// </summary>
            public Vector2 Vector;

            public MapNode(int x, int y, Grid grid)
            {
                this.x = x;
                this.y = y;
                Index = new Vector2Int(x, y);
                Grid = grid;
                Vector = Grid.Pathfinder.ConvertToLevelCoordinates(x, y);
                Clearance = 1;
                OriginalClearance = Clearance;
                Id = grid.TotalNodes;
                ContainerNode = this;
                //Id = x >= y ? x * x + x + y : x + y * y;  // Szudzik's function
            }
            public void DebugNodeImage(int scale = 2)
            {
                if (ContainerNode != this)
                {
                    ContainerNode.DebugNodeImage(scale);
                }
                Texture2D texture = new Texture2D(Grid.Width * scale, Grid.Height * scale, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                MapNode node;
                HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
                for (int y = 0; y < Grid.Height * scale; y += scale)
                {
                    for (int x = 0; x < Grid.Width * scale; x += scale)
                    {
                        Color color = Color.yellow; // has not been checked
                        node = Grid.Nodes[x / scale][y / scale];
                        if (checkedNodes.Contains(node))
                        {
                            continue;
                        }
                        if (node.Clearance == 0) // obstacle
                        {
                            color = ConfigData.GetUIColor("bad");
                        }
                        else if (node != this) // not an obstacle, checked, and part of the path
                        {
                            color = Color.grey;
                        }

                        if (node == this)
                        {
                            //Debug.Log($"Found the node to debug! {node}");
                            List<MapNode> children = Children.ToList();
                            children.ForEach((childNode) =>
                            {
                                color = Color.blue;

                                for (int v = 0; v < scale; v++)
                                {
                                    for (int h = 0; h < scale; h++)
                                    {
                                        texture.SetPixel((childNode.x * scale) + h, (Grid.Height * scale) - ((childNode.y * scale) + (1 - v)), color); // regular
                                    }
                                }
                                checkedNodes.Add(childNode);
                            });

                            Neighbors.ToList().ForEach((neighborNode) =>
                            {
                                color = Color.red;

                                for (int v = 0; v < scale; v++)
                                {
                                    for (int h = 0; h < scale; h++)
                                    {
                                        texture.SetPixel((neighborNode.x * scale) + h, (Grid.Height * scale) - ((neighborNode.y * scale) + (1 - v)), color); // regular
                                    }
                                }
                                checkedNodes.Add(neighborNode);
                            });

                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Grid.Height * scale) - ((node.y * scale) + (1 - v)), Color.red); // regular
                                }
                            }
                            checkedNodes.Add(node);

                        }
                        else
                        {
                            for (int v = 0; v < scale; v++)
                            {
                                for (int h = 0; h < scale; h++)
                                {
                                    texture.SetPixel((node.x * scale) + h, (Grid.Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                                }
                            }

                        }


                    }
                }
                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{Id}_{Utilities.Hash()}.png";
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            public List<MapNode> GetNeighbors()
            {
                int minY = Mathf.Clamp(y - Clearance, 0, Grid.Height -1);
                int minX = Mathf.Clamp(x - Clearance, 0, Grid.Width - 1);
                int maxY = Mathf.Clamp(y + Clearance, 0, Grid.Height - 1);
                int maxX = Mathf.Clamp(x + Clearance, 0, Grid.Width - 1);

                //Debug.Log($"minX {minX}, maxX {maxX}, minY {minY}, maxY {maxY}");

                // bottom border
                for (int boundsX = minX; boundsX <= maxX; boundsX++)
                {
                    MapNode loopNode = Grid.Nodes[boundsX][maxY];
                    if (loopNode.Clearance > 0)
                    {
                        Neighbors.Add(loopNode.ContainerNode);
                    }
                }

                // top border
                for (int boundsX = minX; boundsX <= maxX; boundsX++)
                {
                    MapNode loopNode = Grid.Nodes[boundsX][minY];
                    if (loopNode.Clearance > 0)
                    {
                        Neighbors.Add(loopNode.ContainerNode);
                    }
                }

                // right border
                for (int boundsY = maxY - 1; boundsY > minY; boundsY--)
                {
                    MapNode loopNode = Grid.Nodes[maxX][boundsY];
                    if (loopNode.Clearance > 0)
                    {
                        Neighbors.Add(loopNode.ContainerNode);
                    }
                }

                // left border
                for (int boundsY = maxY - 1; boundsY > minY; boundsY--)
                {
                    MapNode loopNode = Grid.Nodes[minX][boundsY];
                    if (loopNode.Clearance > 0)
                    {
                        Neighbors.Add(loopNode.ContainerNode);
                    }
                }

                return Neighbors.ToList();
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
            protected string Info()
            {
                return $"MapNode #{Id} - {(IsPermanant ? "Y" : "N")}: ({x}, {y}), Clearance: {Clearance}, Container: #{ContainerNode.Id}, Children: {Children.Count}\n";
            }
            public override string ToString()
            {
                string toString = Info() +
                    $"Neighbors ({Neighbors.Count}):\n\n";
                for (int i = 0; i < Neighbors.Count && i < 10; i++)
                {
                    toString += Neighbors.ElementAt(i).Info();
                }
                toString += $"Children ({Children.Count}):\n\n";
                for (int i = 0; i < Children.Count && i < 10; i++)
                {
                    toString += Children.ElementAt(i).Info();
                }
                return toString;

            }
            public string ToJson()
            {
                string json = $"{{\"x\": {x}, \"y\": {y}, \"OC\": {OriginalClearance}, \"C\": [";
                Children.ToList().ForEach((n) => json += $" {{ \"x\": {n.x}, \"y\": {n.y} }},");
                if (Children.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }

                json += "], \"N\": [";
                Neighbors.ToList().ForEach((n) => json += $" {{ \"x\": {n.x}, \"y\": {n.y} }},");

                if (Neighbors.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }
                
                json += "]}";

                return json;
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
        }
    }

}

