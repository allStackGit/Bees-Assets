using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SW = System.Diagnostics;
using UnityEngine;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Assets.Scripts.Level
{

    public class Pathfinder
    {

        public const int DIAGONAL_COST = 14;
        public const int HORIZONTAL_COST = 10;
        public List<MapNode> UncheckedNodes;
        public HashSet<MapNode> UncheckedNodesSet;
        public HashSet<MapNode> CheckedNodes = new HashSet<MapNode>();
        public List<MapNode> ResettableNodes;
        public HashSet<Path> PathCache = new HashSet<Path>();
        public HashSet<Vector2Int> FreeAreas = new HashSet<Vector2Int>();
        public const float TimeLimit = 5;
        public int DebugLoops = 0;
        public int MaxLoopsPerFrame = 1000;

        private Grid _grid;
        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly
        /// </summary>
        public int Scale = 4;
        public int Width, Height, HalfWidth, HalfHeight;
        public LevelStage Level;
        public List<Obstacle> Obstacles = new List<Obstacle>();
        /// <summary>
        /// A list of arrays of obstacle points. Each array of points belongs to an obstacle and each point (a two int array) is an x index and a y index on the Map array
        /// </summary>
        public List<int[][]> ObstaclePoints = new List<int[][]>();
        public bool HasMovingObstacles;

        public Pathfinder(LevelStage level)
        {
            Level = level;
            Width = (int)Math.Ceiling((double)Level.MapWidth / Scale);
            Height = (int)Math.Ceiling((double)Level.MapHeight / Scale);
            HalfWidth = (int)Math.Ceiling((double)Level.HalfMapWidth / Scale);
            HalfHeight = (int)Math.Ceiling((double)Level.HalfMapHeight / Scale);

            //Level.StartCoroutine(InitializeMap());
            InitializeMap();

        }
        public void Update()
        {
            if (PathsWaiting.Count > 0)
            {
               

                for (int i = 0; i < ConfigData.MaxThreads && PathsWaiting.Count > 0; i++)
                {
                    //Debug.Log($"Checking thread #{i} : {IsThreadActive[i]}, Pathswaiting: {PathsWaiting.Count}");
                    if (!IsThreadActive[i])
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


                        IsThreadActive[i] = true;
                        Clearances[i] = p.Clearance;

                        if (Level.ActivateCollisionAsteroids)
                        {
                            UpdateMapTime[i] = SW.Stopwatch.StartNew();
                            UpdateMap(i, p.Ship);
                            UpdateMapTime[i].Stop();
                        }
                        IsThreadActive[i] = true;
                        StartNodes[i] = GridNodes[i][p.Start.x][p.Start.y];
                        EndNodes[i] = GridNodes[i][p.End.x][p.End.y];
                        Ships[i] = p.Ship;

                        //Debug.Log($"Queued Started BT #{i}");

                        //Debug.Log($"Queued running #{i} for {p.Ship.Name}");

                        BTFindPath(i);

                        //Debug.Log($"Called BTFindPath({i}) #{i} for {p.Ship.Name}");

                        //Debug.Log($"Thread #{i} Queued Started {p.Ship.Name} after waiting {(Time.realtimeSinceStartup - p.StartTime) * 1000}ms on the queue");

                        //ThreadsStarted++;
                        //PathsWaitingToRemove.Add(p);
                        ShipsQueued.Remove(p.Ship);
                        //break;
                    }
                }

                //Debug.Log($"There are NOW {PathsWaiting.Count} paths waiting at {Time.realtimeSinceStartup}");
            }
            
        }

        //int totalLoopCount = 0;
        int minY, minX, maxY, maxX, boundsX, boundsY, x, y, yMovement = 0;
        bool hasHitObstacle;
        MapNode currentNode, loopNode, previousNode;
        public void CalculateClearance(MapNode[][] nodes, int startX, int endX, int startY, int endY, int maxClearance, bool isSubSection)
        {
            //float start = Time.realtimeSinceStartup;
            

            for (y = startY; y < endY; y++)
            {
                yMovement = 1;
                for (x = startX; x < endX; x++)
                {
                    //try
                    //{
                    //    currentNode = nodes[x][y];
                    //}
                    //catch (Exception e)
                    //{
                    //    Debug.Log($"startX: {startX}, startY: {startY}, endX: {endX}, endY: {endY}, x: {x}, y: {y}, width: {_grid.Width}, height: {_grid.Height} ");
                    //    throw e;
                    //}
                    previousNode = currentNode;
                    currentNode = nodes[x][y];
                    //Debug.Log($"CN: ({x}, {y}) => ({currentNode.x}, {currentNode.y})");
                    if (currentNode.Clearance > 0)
                    {
                        // if this isn't the first node of the row and the previous node had clearance, then we know we've moved to the right and as long as the entire right side is clear,
                        // we have at least as much clearance as the previous node
                        if (yMovement == 0 && previousNode.Clearance > 0 && x + 1 < endX)
                        {
                            for (boundsY = (previousNode.y + previousNode.Clearance); boundsY > (previousNode.y - previousNode.Clearance); boundsY--)
                            {
                                //totalLoopCount++;
                                //loopNode = _grid.GetNode(maxX, boundsY);
                                loopNode = nodes[currentNode.x + 1][boundsY];
                                //try
                                //{
                                //    loopNode = nodes[currentNode.x + 1][boundsY];
                                //}
                                //catch (Exception e)
                                //{
                                //    Debug.Log($"startX: {startX}, startY: {startY}, endX: {endX}, endY: {endY}, x: {currentNode.x + 1}, y: {boundsY}, " +
                                //        $"clearance: {previousNode.Clearance}, node: ({previousNode.x}, {previousNode.y})");
                                //    throw e;
                                //}
                                //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                if (loopNode.Clearance == 0)
                                {
                                    hasHitObstacle = true;
                                    //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                    break;
                                }
                                currentNode.Clearance++;
                            }
                            //if (!hasHitObstacle)
                            //{
                            //    currentNode.Clearance = previousNode.Clearance;
                            //}
                        }
                        else
                        {
                            currentNode.Clearance = 1;
                        }
                        hasHitObstacle = false;
                        minY = currentNode.y - currentNode.Clearance;
                        minX = currentNode.x - currentNode.Clearance;
                        maxY = currentNode.y + currentNode.Clearance;
                        maxX = currentNode.x + currentNode.Clearance;
                        while (!hasHitObstacle && currentNode.Clearance < maxClearance && maxX < _grid.Width && maxY < _grid.Height && minX >= 0 && minY >= 0)
                        {
                            // bottom border
                            //Debug.Log($"Checking clearance ({currentNode.Clearance+1}) for {currentNode.Index}: minX: {minX}, maxX: {maxX}, minY: {minY}, maxY: {maxY}");
                            for (boundsX = minX; boundsX <= maxX; boundsX++)
                            {
                                //totalLoopCount++;
                                //loopNode = _grid.GetNode(boundsX, maxY);
                                loopNode = nodes[boundsX][maxY];

                                //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                if (loopNode.Clearance == 0)
                                {
                                    //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                    hasHitObstacle = true;
                                    break;
                                }
                                //Debug.Log($"{loopNode.Index} is being added as a child of {currentNode}");
                            }

                            // top border
                            if (!hasHitObstacle)
                            {
                                for (boundsX = minX; boundsX <= maxX; boundsX++)
                                {
                                    //totalLoopCount++;
                                    //loopNode = _grid.GetNode(boundsX, minY);
                                    loopNode = nodes[boundsX][minY];
                                    //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                    if (loopNode.Clearance == 0)
                                    {
                                        //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                        hasHitObstacle = true;
                                        break;
                                    }
                                }

                                // right border
                                if (!hasHitObstacle)
                                {
                                    for (boundsY = maxY - 1; boundsY > minY; boundsY--)
                                    {
                                        //totalLoopCount++;
                                        //loopNode = _grid.GetNode(maxX, boundsY);
                                        loopNode = nodes[maxX][boundsY];
                                        //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                        if (loopNode.Clearance == 0)
                                        {
                                            hasHitObstacle = true;
                                            //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                            break;
                                        }
                                    }

                                    // left border
                                    if (!hasHitObstacle)
                                    {
                                        for (boundsY = maxY - 1; boundsY > minY; boundsY--)
                                        {
                                            //totalLoopCount++;
                                            //loopNode = _grid.GetNode(minX, boundsY);
                                            loopNode = nodes[minX][boundsY];
                                            //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                            if (loopNode.Clearance == 0)
                                            {
                                                hasHitObstacle = true;
                                                //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                                break;
                                            }
                                        }

                                        if (!hasHitObstacle)
                                        {
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
                        if (!isSubSection)
                        {
                            currentNode.OriginalClearance = currentNode.Clearance;
                        }
                    } // end of current node calculation
                } // end of x loop
                yMovement = 0;

                //if (!isSubSection)
                //{
                //    yield return ConfigData.WaitForEndOfFrame;
                //}
            } // end of y loop
            //if (!isSubSection)
            //{
            //    float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            //    //SaveClearanceMap();
            //    Debug.Log($"calculateClearance() took {end} ms to complete. There were {totalLoopCount} loops measuring clearance");
            //}
            //Level.StartCoroutine(CalculateSquares());
        }
        public void InitializeMap()
        {
            float start = Time.realtimeSinceStartup;
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
                obstacle.MapPointsIndex = AddObstacle(obstacle);
                ObstaclePoints[obstacle.MapPointsIndex] = GetObstaclePoints(obstacle, 0, 0);

                //Debug.Log($"The first point on {obstacle.Name} is ({ObstaclePoints[obstacle.Id][0][0]}, {ObstaclePoints[obstacle.Id][0][1]}) on the map");

                foreach (int[] point in ObstaclePoints[obstacle.MapPointsIndex])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                        _grid.Nodes[point[0]][point[1]].Clearance = 0; // set to unwalkable space
                        _grid.Nodes[point[0]][point[1]].OriginalClearance = 0;
                    }
                    else if (!obstacle.IsMapBorder)
                    {
                        Debug.Log($"Invalid indexes: {point[0]}, {point[1]}");
                    }
                }
            }

            CalculateClearance(_grid.Nodes, 0, _grid.Width, 0, _grid.Height, int.MaxValue, false);

            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
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

                // Get neighbors for each node
                for (int x = 0; x < _grid.Width; x++)
                {
                    for (int y = 0; y < _grid.Height; y++)
                    {
                        GridNodes[i][x][y].GetNeighbors(GridNodes[i]);
                    }
                }
            }

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"Initialized map in {end} ms");


        }
        public int AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
            ObstaclePoints.Add(new int[][] { });

            if (obstacle.IsMobile && !HasMovingObstacles)
            {
                HasMovingObstacles = true;
            }
            //Debug.Log($"Adding {obstacle.Name} to Map");
            return Obstacles.Count - 1;
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

            Vector2 position = obstacle.GetPosition();
            Vector2 bounds = collider.bounds.size;


            int width = (int)Math.Ceiling(bounds.x);
            int height = (int)Math.Ceiling(bounds.y);
            int startX = (int)Math.Floor(position.x - (width / 2));
            int startY = (int)Math.Floor(position.y + (height / 2));

            //Debug.Log($"Checking points on {obstacle.Name} starting at {startX} and going across {width}");

            List<int[]> points = new List<int[]>();

            for (int x = startX; x < startX + width; x += Scale) // go across the bounds, left to right (increasing)
            {
                for (int y = startY; y > startY - height; y -= Scale)
                {
                    Vector2Int point = new Vector2Int(x, y);
                    if (collider.OverlapPoint(point))
                    {
                        Vector2Int converted = ConvertToMapCoordinates(point);
                        points.Add(new int[] { converted.x, converted.y });

                        if (yVelocity != 0 || xVelocity != 0)
                        {

                            point.y += (int)Math.Round(yVelocity * 2);
                            point.x += (int)Math.Round(xVelocity * 2);


                            converted = ConvertToMapCoordinates(point);
                            points.Add(new int[] { converted.x, converted.y });
                        }

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
        public void UpdateMap(int thread, Ship ship)
        {
            float start = Time.realtimeSinceStartup;
            CollisionAsteroid asteroid;
            List<CollisionAsteroid> collisionAsteroids = ship.NearbyAsteroids; // is the ToList() necessary? // only if we use a HashSet
            int leastY = int.MaxValue;
            int leastX = int.MaxValue;
            int mostX = int.MinValue;
            int mostY = int.MinValue;
            int sectionSize = 20;
            int startX;
            int startY;
            int endX;
            int endY;
            int totalAsteroids = PreviousAsteroids[thread].Count + collisionAsteroids.Count;
            int fullClearanceThreshold = 3;
            //Debug.Log($"Updating map");

            

            PreviousAsteroids[thread].ForEach((asteroidId) =>
            {
                float asteroidTime = Time.realtimeSinceStartup;
                //Debug.Log($"Clearing the position of {asteroid.Name} on the pathfinding map");
                foreach (int[] point in ObstaclePoints[asteroidId])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        GridNodes[thread][point[0]][point[1]].Clearance = GridNodes[thread][point[0]][point[1]].OriginalClearance; // set its old position to the original clearance

                        if (point[0] < leastX)
                        {
                            leastX = point[0];
                        }
                        else if (point[0] > mostX)
                        {
                            mostX = point[0];
                        }

                        if (point[1] < leastY)
                        {
                            leastY = point[1];
                        }
                        else if (point[1] > mostY)
                        {
                            mostY = point[1];
                        }
                    }
                }

                startX = Math.Clamp(leastX - sectionSize, 0, _grid.Width - 1);
                startY = Math.Clamp(leastY - sectionSize, 0, _grid.Height - 1);
                endX = Math.Clamp(mostX + sectionSize, 0, _grid.Width - 1);
                endY = Math.Clamp(mostY + sectionSize, 0, _grid.Height - 1);

                if (totalAsteroids < fullClearanceThreshold)
                {
                    CalculateClearance(GridNodes[thread], startX, endX, startY, endY, Clearances[thread], true);
                    //Debug.Log($"Calculated clearance around asteroid #{asteroidId} in {(Time.realtimeSinceStartup - asteroidTime) * 1000}ms");
                }
            });
            PreviousAsteroids[thread].Clear();

            for (int i = 0; i < collisionAsteroids.Count; i++)
            {
                float asteroidTime = Time.realtimeSinceStartup;
                asteroid = collisionAsteroids[i];

                if (asteroid != null)
                {
                    // Get the direction the asteroid is moving in. Negative Y is down, Negative X is left.
                    //Debug.Log($"Nearby asteroid {asteroid.Name} is moving in {asteroid.Body.velocity} direction");
                    // Get the new points
                    //Debug.Log($"Updating the position of {asteroid.Name} on the pathfinding map");
                    //float obstaclePoints = Time.realtimeSinceStartup;
                    ObstaclePoints[asteroid.MapPointsIndex] = GetObstaclePoints(asteroid, asteroid.Body.velocity.x, asteroid.Body.velocity.y);
                    //float obstaclePointsEnd = (Time.realtimeSinceStartup - obstaclePoints) * 1000; // seconds to milliseconds
                    //Debug.Log($"Updated obstacle points in {obstaclePointsEnd} ms"); // takes less than a millisecond
                    //ObstaclePoints[asteroid.MapPointsIndex] = GetObstaclePoints(asteroid, 0, 0);

                    //Debug.Log($"Got obstacle points for {asteroid}");

                    foreach (int[] point in ObstaclePoints[asteroid.MapPointsIndex])
                    {
                        if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                        {
                            //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                            GridNodes[thread][point[0]][point[1]].Clearance = 0; // set its new position to unwalkable space
                            if (point[0] < leastX)
                            {
                                leastX = point[0];
                            }
                            else if (point[0] > mostX)
                            {
                                mostX = point[0];
                            }

                            if (point[1] < leastY)
                            {
                                leastY = point[1];
                            }
                            else if (point[1] > mostY)
                            {
                                mostY = point[1];
                            }

                        }
                    }
                    PreviousAsteroids[thread].Add(asteroid.MapPointsIndex);
                    startX = Math.Clamp(leastX - sectionSize, 0, _grid.Width - 1);
                    startY = Math.Clamp(leastY - sectionSize, 0, _grid.Height - 1);
                    endX = Math.Clamp(mostX + sectionSize, 0, _grid.Width - 1);
                    endY = Math.Clamp(mostY + sectionSize, 0, _grid.Height - 1);

                    if (totalAsteroids < fullClearanceThreshold)
                    {
                        CalculateClearance(GridNodes[thread], startX, endX, startY, endY, Clearances[thread], true);
                        //Debug.Log($"Set obstacle points and possibly calculated clearance around {asteroid.Name} in {(Time.realtimeSinceStartup - asteroidTime) * 1000}ms");

                    }

                }
            }
            if (totalAsteroids >= fullClearanceThreshold)
            {
                float fullCalcTime = Time.realtimeSinceStartup;
                CalculateClearance(GridNodes[thread], 0, _grid.Width, 0, _grid.Height, Clearances[thread], true);
                Debug.Log($"Calculated full clearance in {(Time.realtimeSinceStartup - fullCalcTime) * 1000}ms");

            }
            //CalculateClearance(GridNodes[thread], true);

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"Updated map with {PreviousAsteroids[thread].Count} previous asteroids and {collisionAsteroids.Count} collision asteroids in {end} ms\n" +
                $"Previous asteroids: {string.Join(",", PreviousAsteroids[thread])} \n" +
                $"Collision asteroids: {string.Join(",", collisionAsteroids.Select((a) => $"#{a.Id}"))}");

        }

        public MapNode FindNearestWalkablePoint(MapNode startNode, MapNode endNode, int minimumClearance, MapNode[][] nodes)
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
            MapNode directionNode = nodes[endNode.x + xInterval][endNode.y + yInterval];
            MapNode oppositeDirectionNode = nodes[endNode.x - xInterval][endNode.y - yInterval];

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
                else if (xIncrease < 0)
                {
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
                directionNode = nodes[xIncrease][yIncrease];
                oppositeDirectionNode = nodes[xDecrease][yDecrease];
            }
            if (loops == 100) // [debug]
            {
                Debug.Log($"The loop broke after 100 loops trying to find a walkable point near {endNode.Vector} starting from {startNode.Vector}");
            }
            return null;

        }
        private void MakeDestinationList(MapNode BTEndNode, Path BTPath)
        {
            List<Vector2> BTDestinationList = new List<Vector2> { BTEndNode.Vector };
            
            MapNode BTCurrentNode = BTEndNode;

            while (BTCurrentNode.PreviousNode != MapNode.NullNode)
            {
                //Debug.Log(currentNode.PreviousNode.Id);
                BTDestinationList.Add(BTCurrentNode.PreviousNode.Vector);
                BTCurrentNode = BTCurrentNode.PreviousNode;

                BTCurrentNode.PreviousNode.IsPartOfPath = true;

            }

            BTDestinationList.Reverse();
            //path.SetPoints(destinationList);
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
        public Thread[] Threads = new Thread[ConfigData.MaxThreads];
        public bool[] IsThreadActive = new bool[ConfigData.MaxThreads];
        public List<int>[] PreviousAsteroids = new List<int>[ConfigData.MaxThreads];
        //public int ThreadsStarted = -1;
        //public int ThreadIndex;
        //public int Thread => ThreadsStarted % ConfigData.MaxThreads;
        public SW.Stopwatch[] Totals = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] NeighborLoops = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] GetNodes = new SW.Stopwatch[ConfigData.MaxThreads];
        public SW.Stopwatch[] UpdateMapTime = new SW.Stopwatch[ConfigData.MaxThreads];
        public MapNode[] StartNodes = new MapNode[ConfigData.MaxThreads];
        public MapNode[] EndNodes = new MapNode[ConfigData.MaxThreads];
        public int[] Clearances = new int[ConfigData.MaxThreads];
        public Ship[] Ships = new Ship[ConfigData.MaxThreads];
        public MapNode[][][] GridNodes = new MapNode[ConfigData.MaxThreads][][];



        public class PathWaiting
        {
            public Ship Ship;
            public MapNode Start;
            public MapNode End;
            public int Clearance;
            public float StartTime = Time.realtimeSinceStartup;

            public PathWaiting(Ship ship, MapNode start, MapNode end, int clearance)
            {
                Ship = ship;
                Start = start;
                End = end;
                Clearance = clearance;
            }
        }

        public void OrderPrintDebugImage(int index)
        {
            Ships[index].DebugGrid = _grid;
            Ships[index].DebugNodes = GridNodes[index];
            Ships[index].DebugEndNode = EndNodes[index];
            Ships[index].DebugStartNode = StartNodes[index];
            Ships[index].PrintDebugImage = true;
            IsThreadActive[index] = false;
        }
        public async Task BTFindPath(int index)
        {
            await Task.Run(() =>
            {
                //Debug.Log($"_Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{index}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
                //Debug.Log($"_Started BT #{index} : {Ships[index].Name}");
                //minimumClearance = 1;
                //maximumClearance = 1;
                //Debug.Log($"Trying to find a path for #{index} from ({StartNodes[index].x}, {StartNodes[index].y}) to ({EndNodes[index].x}, {EndNodes[index].y})");
                Totals[index] = SW.Stopwatch.StartNew();
                NeighborLoops[index] = SW.Stopwatch.StartNew();
                GetNodes[index] = SW.Stopwatch.StartNew();


                int BTLoops = 0;
                int BTTempCostToHere;


                for (int x = 0; x < _grid.Width; x++)
                {
                    for (int y = 0; y < _grid.Height; y++)
                    {
                        GridNodes[index][x][y].CostToHere = int.MaxValue;
                        GridNodes[index][x][y].TotalCost = int.MaxValue;
                        GridNodes[index][x][y].PreviousNode = MapNode.NullNode;

                        GridNodes[index][x][y].HasBeenChecked = false;
                        GridNodes[index][x][y].IsPartOfPath = false;

                    }
                }


                //Debug.Log($"Finished grid loops BT #{index}");


                //Debug.Log($"BTS: {StartNodes[index]}");
                //Debug.Log($"BTE: {EndNodes[index]}");

                //OrderPrintDebugImage(index);
                //return;
                if (EndNodes[index].Clearance < Clearances[index])
                {
                    //Debug.Log($"The end ({EndNodes[index].Vector}) for #{index} : {Ships[index].Name} isn't walkable space");
                    EndNodes[index] = FindNearestWalkablePoint(StartNodes[index], EndNodes[index], Clearances[index], GridNodes[index]);
                    //Debug.Log($"Found new end point that is walkable: {EndNodes[index]}");
                }

                if (StartNodes[index].Clearance < Clearances[index])
                {
                    //Debug.Log($"The start ({startNode.Vector}) isn't walkable space");
                    StartNodes[index] = FindNearestWalkablePoint(EndNodes[index], StartNodes[index], Clearances[index], GridNodes[index]);
                    //Debug.Log($"Found new start point that is walkable: {StartNodes[index]}");
                }
                //Debug.Log($"Starting at {startNode}");
                Path BTPath = new Path(StartNodes[index].x, StartNodes[index].y, EndNodes[index].x, EndNodes[index].y);

                List<MapNode> BTUncheckedNodes = new List<MapNode>() { StartNodes[index] };
                HashSet<MapNode> BTUncheckedNodesSet = new HashSet<MapNode> { StartNodes[index] };
                //SortedNodes = new SortedDictionary<int, MapNode> { { startNode.SortingId, startNode }  };
                HashSet<MapNode> BTCheckedNodes = new HashSet<MapNode>();

                //Debug.Log($"Initialized vars BT #{index}");

                StartNodes[index].CostToHere = 0;
                StartNodes[index].HueristicCost = MapNode.CalculateDistance(StartNodes[index], EndNodes[index]);
                StartNodes[index].CalculateTotalCost();
                //Debug.Log($"Initialized startnode BT #{index}");
                //if (startNode.ContainerNode != startNode)
                //{
                //    startNode.Neighbors.Add(startNode.ContainerNode);
                //}

                //loops = 0;
                MapNode BTPreviousNode = StartNodes[index];
                //Debug.Log($"Initialized previous BT #{index}");
                double BTStartupTime = Totals[index].Elapsed.TotalMilliseconds;
                //Debug.Log($"Startup time took: {BTStartupTime} ms");
                //Debug.Log($"Startnode: {startNode}, queueLoops: {queueLoops}, clearanceMapList: {_grid.ClearanceMapList.Count}");
                MapNode BTCurrentNode = MapNode.NullNode;
                while (BTUncheckedNodes.Count > 0 && GetNodes[index].Elapsed.TotalSeconds < TimeLimit)
                {
                    GetNodes[index].Start();
                    BTLoops++;
                    //if (BTLoops % 100 == 0)
                    //{
                    //    Debug.Log($"Loop #{BTLoops}");
                    //}

                    BTCurrentNode = GetCheapestNode(BTUncheckedNodes, BTPreviousNode);

                    BTPreviousNode = BTCurrentNode;

                    if (BTCurrentNode == EndNodes[index])
                    {
                        //Debug.Log($"Finished background finding path #{index}:{Ships[index].Name}.");
                        MakeDestinationList(EndNodes[index], BTPath);
                        Totals[index].Stop();
                        GetNodes[index].Stop();
                        //Debug.Log($"Finished background finding path and destination list for #{index}:{Ships[index].Name}. ({BTPath.Points.Count}) Loops: ({BTLoops}) startup time: {BTStartupTime}ms, getNode Time: {GetNodes[index].Elapsed.TotalMilliseconds}ms, " +
                            //$"neighborLoop Time: {NeighborLoops[index].Elapsed.TotalMilliseconds}ms, Update Map Time: {UpdateMapTime[index].Elapsed.TotalMilliseconds}ms Total: {(Totals[index].Elapsed.TotalMilliseconds)}ms");
                        Ships[index].PathfindingValue = BTPath;
                        Ships[index].PathfindingThreadComplete = true;
                        IsThreadActive[index] = false;

                        OrderPrintDebugImage(index);
                        return;
                    }
                    BTUncheckedNodes.Remove(BTCurrentNode);
                    BTUncheckedNodesSet.Remove(BTCurrentNode);

                    BTCheckedNodes.Add(BTCurrentNode);
                    BTCurrentNode.HasBeenChecked = true;

                    //Debug.Log($"Getting neighbors for {currentNode}");
                    GetNodes[index].Stop();
                    NeighborLoops[index].Start();

                    BTCurrentNode.Neighbors.ForEach((neighbor) =>
                    {
                        if (!BTCheckedNodes.Contains(neighbor))
                        {
                            //Debug.Log($"Neighbor: {neighbor}");
                            if (neighbor.Clearance >= Clearances[index]) // < maximum clearance                 == 0
                            {
                                //Debug.Log($"Passed clearance");
                                BTTempCostToHere = BTCurrentNode.CostToHere + MapNode.CalculateDistance(BTCurrentNode, neighbor);
                                if (BTTempCostToHere < neighbor.CostToHere)
                                {
                                    //Debug.Log($"Lower Cost");
                                    neighbor.PreviousNode = BTCurrentNode;
                                    neighbor.CostToHere = BTTempCostToHere;
                                    neighbor.HueristicCost = MapNode.CalculateDistance(neighbor, EndNodes[index]);
                                    neighbor.CalculateTotalCost();
                                    //UncheckedNodes.Add(neighbor);
                                    if (!BTUncheckedNodesSet.Contains(neighbor))
                                    {
                                        BTUncheckedNodes.Add(neighbor);
                                        BTUncheckedNodesSet.Add(neighbor);
                                    }
                                }


                            }
                        }
                    });

                    NeighborLoops[index].Stop();

                }


                if (GetNodes[index].Elapsed.TotalSeconds > TimeLimit)
                {
                    Debug.Log($"Ran out of time while trying to find a path #{index}");
                }
                else if (BTUncheckedNodes.Count == 0)
                {
                    Debug.Log($"No more nodes to check for ship: {Ships[index].Name} index: #{index} Clearance: {Clearances[index]}.  checkedNodes: {BTCheckedNodes.Count} / {_grid.TotalNodes}  CurrentNode: {BTCurrentNode},");
                }
                Ships[index].PathfindingThreadComplete = true;
                IsThreadActive[index] = false;


                OrderPrintDebugImage(index);
            });
            

        }

        private MapNode startNode, endNode;
        public void FindPath(Ship ship, int startX, int startY, int endX, int endY, int maximumClearance)
        {

            //try
            //{
            //    startNode = _grid.Nodes[startX][startY];
            //    endNode = _grid.Nodes[endX][endY];
            //}
            //catch (Exception e)
            //{
            //    Debug.Log($"startX: {startX}, startY: {startY}, endX: {endX}, endY: {endY}, width: {_grid.Width}, height: {_grid.Height} ");
            //    throw e;
            //}

            startNode = _grid.Nodes[startX][startY];
            endNode = _grid.Nodes[endX][endY];

            //Debug.Log($"Finding path for ? from {startNode.x}, {startNode.y} to {endNode.x}, {endNode.y}");
            //Debug.Log($"Finding a path for {ship.Name} with clearance {maximumClearance}");

            bool startedTask = false;
            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                if (!IsThreadActive[i])
                {
                    Clearances[i] = maximumClearance;
                    if (Level.ActivateCollisionAsteroids)
                    {
                        UpdateMapTime[i] = SW.Stopwatch.StartNew();
                        UpdateMap(i, ship);
                        UpdateMapTime[i].Stop();
                    }
                    IsThreadActive[i] = true;
                    //Debug.Log($"Pre starting Finding path for #{i} from {startNode.x}, {startNode.y} to {endNode.x}, {endNode.y}");
                    StartNodes[i] = GridNodes[i][startNode.x][startNode.y];
                    EndNodes[i] = GridNodes[i][endNode.x][endNode.y];
                    Ships[i] = ship;

                    BTFindPath(i);
                    //Debug.Log($"Standard Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{i}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
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
            }
            if (!startedTask)
            {
                if (!ShipsQueued.Contains(ship))
                {
                    PathsWaiting.Enqueue(new PathWaiting(ship, startNode, endNode, maximumClearance));
                    ShipsQueued.Add(ship);
                }
            }


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
            return new Vector2Int((int) Math.Round(Level.MapWidth - (Level.HalfMapWidth - coords.x)), (int)Math.Round(Level.MapHeight - (Level.HalfMapHeight + coords.y))) / Scale;
        }
        public Vector2 ConvertToLevelCoordinates(Vector2Int coords)
        {
            return ConvertToLevelCoordinates(coords.x, coords.y);
        }
        public Vector2 ConvertToLevelCoordinates(int x, int y)
        {
            return new Vector2(-HalfWidth + x, HalfHeight - y) * Scale;
        }
        public Vector2Int ConvertToLevelCoordinatesInt(int x, int y)
        {
            return new Vector2Int(-HalfWidth + x, HalfHeight - y) * Scale;
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
            public void DebugGridAsImage(Vector2Int firstNode, Vector2Int lastNode, MapNode[][] nodes, int scale, Ship ship)
            {
                Texture2D texture = new Texture2D(Width * scale, Height * scale, TextureFormat.RGB24, false);
                //Color[] pixels = texture.GetPixels();
                MapNode node;
                for (int y = 0; y < Height * scale; y += scale)
                {
                    for (int x = 0; x < Width * scale; x += scale)
                    {
                        node = nodes[ x/ scale][y / scale];
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
                        texture.SetPixel((lastNode.x * scale) + h, (Height * scale) - ((lastNode.y * scale) + (1 - v)), Color.blue); // last pixel
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((firstNode.x * scale) + h, (Height * scale) - ((firstNode.y * scale) + (1 - v)), Color.red); // first pixel
                    }
                }

                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{ship.ShipType}_{ship.Id}_{Utilities.Hash()}.png";
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
            public List<MapNode> Neighbors = new List<MapNode>();
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
                return $"MapNode #{Id}: ({x}, {y}), PreviousNode: {PreviousNode.Id}, Clearance: {Clearance}, IN: {Neighbors.Count}\n";
            }
            public override string ToString()
            {
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
                string json = $"{{\"Id\": {Id}, \"x\": {x}, \"y\": {y}, \"OC\": {OriginalClearance}, \"N\": [";
                Neighbors.ForEach((n) => json += $" {{ \"Id\": {n.Id}, \"x\": {n.x}, \"y\": {n.y}, \"OC\": {n.OriginalClearance} }},");

                if (Neighbors.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }



                json += "]}";

                return json;
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

