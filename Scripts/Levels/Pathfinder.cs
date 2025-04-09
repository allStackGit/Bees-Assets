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
            return new Vector2Int((int)Math.Round(Level.MapWidth - (Level.HalfMapWidth - coords.x)), (int)Math.Round(Level.MapHeight - (Level.HalfMapHeight + coords.y))) / Scale;
        }
        public Vector2 ConvertToLevelCoordinates(int x, int y)
        {
            return new Vector2(-HalfWidth + x, HalfHeight - y) * Scale;
        }
        public Vector2Int ConvertToLevelCoordinatesInt(int x, int y)
        {
            return new Vector2Int(-HalfWidth + x, HalfHeight - y) * Scale;
        }
        public void Update()
        {
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

                        p.Ship.IsPathfinding = true;
                        IsThreadActive[threadIndex] = true;
                        Clearances[threadIndex] = p.Clearance;

                        if (Level.ActivateCollisionAsteroids)
                        {
                            //UpdateMapTime[i] = SW.Stopwatch.StartNew();
                            UpdateMap(threadIndex, p.Ship);
                            //UpdateMapTime[i].Stop();
                        }
                        StartNodes[threadIndex] = GridNodes[threadIndex][p.StartX][p.StartY];
                        EndNodes[threadIndex] = GridNodes[threadIndex][p.EndX][p.EndY];
                        Ships[threadIndex] = p.Ship;

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
            //loopsSaved = 0;
            //float start = Time.realtimeSinceStartup;

            //Debug.Log($"Trying to calculate clearance for section ({startX}, {startY}) to ({endX}, {endY}) and max clearance of {maxClearance}");
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
                        currentNode.Clearance = 1;
                        // if this isn't the first node of the row and the previous node had clearance, then we know we've moved to the right and as long as the entire right side is clear,
                        // we have at least as much clearance as the previous node
                        if (yMovement == 0 && previousNode.Clearance > 1 && x + 1 < endX)
                        {
                            //Debug.Log($"Found a candidate for side expansion: {previousNode}");
                            hasHitObstacle = false;
                            for (boundsY = (previousNode.y + (previousNode.Clearance - 1)); boundsY > (previousNode.y - previousNode.Clearance); boundsY--)
                            {
                                //Debug.Log($"Checking side point ({currentNode.x + (previousNode.Clearance - 1)}, {boundsY})");
                                //totalLoopCount++;
                                //loopNode = _grid.GetNode(maxX, boundsY);
                                //loopNode = nodes[currentNode.x + 1][boundsY];
                                nextX = currentNode.x + (previousNode.Clearance - 1);
                                if (nextX < _grid.Width)
                                {
                                    loopNode = nodes[nextX][boundsY];
                                    //try
                                    //{
                                    //    loopNode = nodes[nextX][boundsY];
                                    //}
                                    //catch (Exception e)
                                    //{
                                    //    Debug.Log($"startX: {startX}, startY: {startY}, endX: {endX}, endY: {endY}, x: {currentNode.x} (+ {(previousNode.Clearance - 1)}), y: {boundsY}, " +
                                    //        $"clearance: {previousNode.Clearance}, node: ({previousNode.x}, {previousNode.y})");
                                    //    throw e;
                                    //}
                                    //Debug.Log($"Checking {loopNode.Index} as a child of {currentNode}");
                                    if (loopNode.Clearance == 0)
                                    {
                                        hasHitObstacle = true;
                                        currentNode.Clearance = previousNode.Clearance - 1;
                                        //Debug.Log($"{currentNode.Index} Has hit obstacle {loopNode.Index}");
                                        break;
                                    }
                                }
                                else
                                {
                                    hasHitObstacle = true;
                                    currentNode.Clearance = previousNode.Clearance - 1;
                                    break;
                                }

                            }
                            if (!hasHitObstacle)
                            {
                                currentNode.Clearance = previousNode.Clearance;
                            }
                        }

                        //currentNode.Clearance = 1;
                        hasHitObstacle = false;
                        minY = currentNode.y - currentNode.Clearance;
                        minX = currentNode.x - currentNode.Clearance;
                        maxY = currentNode.y + currentNode.Clearance;
                        maxX = currentNode.x + currentNode.Clearance;
                        //if (!hasHitObstacle && maxX < _grid.Width && maxY < _grid.Height && minX >= 0 && minY >= 0 && currentNode.Clearance >= maxClearance)
                        //{
                        //    loopsSaved++;
                        //}
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
                    yMovement = 0;
                } // end of x loop

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
            //Debug.Log($"Saved {loopsSaved} loops while calculating clearance");
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
                ObstaclePoints[0][obstacle.MapPointsIndex] = GetObstaclePoints(obstacle, 0, 0);

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

            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                ObstaclePoints[i] = ObstaclePoints[0].ToList();

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

            Vector2 position = obstacle.GetPosition();
            Vector2 bounds = collider.bounds.size;
            //Debug.Log($"{obstacle.Name} has bounds of {bounds}");

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

                            point.y += (int)Math.Round(yVelocity * 2.5f);
                            point.x += (int)Math.Round(xVelocity * 2.5f);


                            converted = ConvertToMapCoordinates(point);
                            points.Add(new int[] { converted.x, converted.y });
                        }

                    }
                }
            }
            return points.ToArray();
        }
        public void UpdateMap(int threadIndex, Ship ship)
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
            int totalAsteroids = PreviousAsteroids[threadIndex].Count + collisionAsteroids.Count;
            int fullClearanceThreshold = 1; // 3
            //Debug.Log($"Updating map");

            

            PreviousAsteroids[threadIndex].ForEach((asteroidId) =>
            {
                float asteroidTime = Time.realtimeSinceStartup;
                //Debug.Log($"Clearing the position of {asteroid.Name} on the pathfinding map");
                foreach (int[] point in ObstaclePoints[threadIndex][asteroidId])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        GridNodes[threadIndex][point[0]][point[1]].Clearance = GridNodes[threadIndex][point[0]][point[1]].OriginalClearance; // set its old position to the original clearance

                        //if (point[0] < leastX)
                        //{
                        //    leastX = point[0];
                        //}
                        //else if (point[0] > mostX)
                        //{
                        //    mostX = point[0];
                        //}

                        //if (point[1] < leastY)
                        //{
                        //    leastY = point[1];
                        //}
                        //else if (point[1] > mostY)
                        //{
                        //    mostY = point[1];
                        //}
                    }
                }

                if (totalAsteroids < fullClearanceThreshold)
                {
                    startX = Math.Clamp(leastX - sectionSize, 0, _grid.MaxX);
                    startY = Math.Clamp(leastY - sectionSize, 0, _grid.MaxY);
                    endX = Math.Clamp(mostX + sectionSize, 0, _grid.MaxX);
                    endY = Math.Clamp(mostY + sectionSize, 0, _grid.MaxY);

                    CalculateClearance(GridNodes[threadIndex], startX, endX, startY, endY, Clearances[threadIndex], true);
                    //Debug.Log($"Calculated clearance around asteroid #{asteroidId} in {(Time.realtimeSinceStartup - asteroidTime) * 1000}ms");
                }
            });
            PreviousAsteroids[threadIndex].Clear();

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
                    try
                    {
                        ObstaclePoints[threadIndex][asteroid.MapPointsIndex] = GetObstaclePoints(asteroid, asteroid.Body.linearVelocity.x, asteroid.Body.linearVelocity.y);
                    }catch (Exception e)
                    {
                        Debug.Log($"Nearby Asteroids: {Utilities.ListToString(collisionAsteroids)}");
                        Debug.LogError($"Ship: {ship.Name},Obstacle: {asteroid} ThreadIndex: {threadIndex}, MapPointIndex: {asteroid.MapPointsIndex}, length: {ObstaclePoints[threadIndex].Count}");
                        throw e;
                    }
                    //float obstaclePointsEnd = (Time.realtimeSinceStartup - obstaclePoints) * 1000; // seconds to milliseconds
                    //Debug.Log($"Updated obstacle points in {obstaclePointsEnd} ms"); // takes less than a millisecond
                    //ObstaclePoints[asteroid.MapPointsIndex] = GetObstaclePoints(asteroid, 0, 0);

                    //Debug.Log($"Got obstacle points for {asteroid}");

                    foreach (int[] point in ObstaclePoints[threadIndex][asteroid.MapPointsIndex])
                    {
                        if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                        {
                            //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                            GridNodes[threadIndex][point[0]][point[1]].Clearance = 0; // set its new position to unwalkable space
                            //if (point[0] < leastX)
                            //{
                            //    leastX = point[0];
                            //}
                            //else if (point[0] > mostX)
                            //{
                            //    mostX = point[0];
                            //}

                            //if (point[1] < leastY)
                            //{
                            //    leastY = point[1];
                            //}
                            //else if (point[1] > mostY)
                            //{
                            //    mostY = point[1];
                            //}

                        }
                    }
                    PreviousAsteroids[threadIndex].Add(asteroid.MapPointsIndex);


                    if (totalAsteroids < fullClearanceThreshold)
                    {
                        startX = Math.Clamp(leastX - sectionSize, 0, _grid.MaxX);
                        startY = Math.Clamp(leastY - sectionSize, 0, _grid.MaxY);
                        endX = Math.Clamp(mostX + sectionSize, 0, _grid.MaxX);
                        endY = Math.Clamp(mostY + sectionSize, 0, _grid.MaxY);
                        CalculateClearance(GridNodes[threadIndex], startX, endX, startY, endY, Clearances[threadIndex], true);
                        //CalculateClearance(GridNodes[thread], 0, _grid.Width, 0, _grid.Height, Clearances[thread], true);
                        //Debug.Log($"Set obstacle points and possibly calculated clearance around {asteroid.Name} in {(Time.realtimeSinceStartup - asteroidTime) * 1000}ms");

                    }

                }
            }
            if (totalAsteroids >= fullClearanceThreshold)
            {
                float fullCalcTime = Time.realtimeSinceStartup;
                CalculateClearance(GridNodes[threadIndex], 0, _grid.Width, 0, _grid.Height, Clearances[threadIndex], true);
                //Debug.Log($"Calculated full clearance for #{threadIndex}:{Ships[threadIndex]?.Name} in {(Time.realtimeSinceStartup - fullCalcTime) * 1000}ms");

            }
            //CalculateClearance(GridNodes[thread], true);

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            //Debug.Log($"Updated map with {PreviousAsteroids[thread].Count} previous asteroids and {collisionAsteroids.Count} collision asteroids in {end} ms\n" +
            //    $"Previous asteroids: {string.Join(",", PreviousAsteroids[thread])} \n" +
            //    $"Collision asteroids: {string.Join(",", collisionAsteroids.Select((a) => $"#{a.Id}"))}");

        }
        public MapNode FindNearestWalkablePoint(MapNode startNode, MapNode endNode, int minimumClearance, int threadIndex)
        {

            int loops = 0;
            MapNode n = GridNodes[threadIndex][startNode.x][Math.Clamp(startNode.y - 1, 0, _grid.MaxY)];
            MapNode ne = GridNodes[threadIndex][Math.Clamp(startNode.x + 1, 0, _grid.MaxX)][Math.Clamp(startNode.y - 1, 0, _grid.MaxY)];
            MapNode e = GridNodes[threadIndex][Math.Clamp(startNode.x + 1, 0, _grid.MaxX)][startNode.y];
            MapNode se = GridNodes[threadIndex][Math.Clamp(startNode.x + 1, 0, _grid.MaxX)][Math.Clamp(startNode.y + 1, 0, _grid.MaxY)];
            MapNode s = GridNodes[threadIndex][startNode.x][startNode.y + 1];
            MapNode sw = GridNodes[threadIndex][Math.Clamp(startNode.x - 1, 0, _grid.MaxX)][Math.Clamp(startNode.y + 1, 0, _grid.MaxY)];
            MapNode w = GridNodes[threadIndex][Math.Clamp(startNode.x - 1, 0, _grid.MaxX)][startNode.y];
            MapNode nw = GridNodes[threadIndex][Math.Clamp(startNode.x - 1, 0, _grid.MaxX)][Math.Clamp(startNode.y - 1, 0, _grid.MaxY)];

            while (loops < 100)
            {
                //Debug.Log($"Trying to find NWP for #{threadIndex}:{Ships[threadIndex].Name} and on loop #{loops}. Loop started");

                if (n.Clearance >= minimumClearance)
                {
                    return n;
                }
                if (ne.Clearance >= minimumClearance)
                {
                    return ne;
                }
                if (e.Clearance >= minimumClearance)
                {
                    return e;
                }
                if (se.Clearance >= minimumClearance)
                {
                    return se;
                }
                if (s.Clearance >= minimumClearance)
                {
                    return s;
                }
                if (sw.Clearance >= minimumClearance)
                {
                    return sw;
                }
                if (w.Clearance >= minimumClearance)
                {
                    return w;
                }
                if (nw.Clearance >= minimumClearance)
                {
                    return nw;
                }
                //Debug.Log($"Trying to find NWP for #{threadIndex}:{Ships[threadIndex].Name} and on loop #{loops}. Loop after returns");

                Ships[threadIndex].DebugWalkablePointNodes.Add(n);
                Ships[threadIndex].DebugWalkablePointNodes.Add(ne);
                Ships[threadIndex].DebugWalkablePointNodes.Add(e);
                Ships[threadIndex].DebugWalkablePointNodes.Add(se);
                Ships[threadIndex].DebugWalkablePointNodes.Add(s);
                Ships[threadIndex].DebugWalkablePointNodes.Add(sw);
                Ships[threadIndex].DebugWalkablePointNodes.Add(w);
                Ships[threadIndex].DebugWalkablePointNodes.Add(nw);


                //Debug.Log($"Trying to find NWP for #{threadIndex}:{Ships[threadIndex].Name} and on loop #{loops}. Loop after debug adds");

                n = GridNodes[threadIndex][n.x][Math.Clamp(n.y - 1, 0, _grid.MaxY)];
                //Debug.Log($"n: {n}");

                ne = GridNodes[threadIndex][Mathf.Clamp(ne.x + 1, 0, _grid.MaxX)][Math.Clamp(ne.y - 1, 0, _grid.MaxY)];
                //Debug.Log($"ne: {ne}");

                e = GridNodes[threadIndex][Mathf.Clamp(e.x + 1, 0, _grid.MaxX)][e.y];
                //Debug.Log($"e: {e}");

                se = GridNodes[threadIndex][Mathf.Clamp(se.x + 1, 0, _grid.MaxX)][Math.Clamp(se.y + 1, 0, _grid.MaxY)];
                //Debug.Log($"se: {se}");

                s = GridNodes[threadIndex][s.x][Math.Clamp(s.y + 1, 0, _grid.MaxY)];
                //Debug.Log($"s: {s}");

                sw = GridNodes[threadIndex][Mathf.Clamp(sw.x - 1, 0, _grid.MaxX)][Math.Clamp(sw.y + 1, 0, _grid.MaxY)];
                //Debug.Log($"sw: {sw}");

                w = GridNodes[threadIndex][Mathf.Clamp(w.x - 1, 0, _grid.MaxX)][w.y];
                //Debug.Log($"w: {w}");

                nw = GridNodes[threadIndex][Mathf.Clamp(nw.x - 1, 0, _grid.MaxX)][Math.Clamp(nw.y - 1, 0, _grid.MaxY)];
                //Debug.Log($"nw: {nw}");

                //Debug.Log($"Trying to find NWP for #{threadIndex}:{Ships[threadIndex].Name} and on loop #{loops}. Loop ended");
                loops++;
            }

            if (loops == 100) // [debug]
            {
                //Debug.Log($"The loop for #{threadIndex}:{Ships[threadIndex].Name} broke after 100 loops trying to find a walkable point near {endNode.Vector} starting from {startNode.Vector}");
            }
            return startNode;

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
        public Ship[] Ships = new Ship[ConfigData.MaxThreads];
        public MapNode[][][] GridNodes = new MapNode[ConfigData.MaxThreads][][];

        public class PathWaiting
        {
            public Ship Ship;
            public int Clearance, StartX, StartY, EndX, EndY;
            public float StartTime = Time.realtimeSinceStartup;

            public PathWaiting(Ship ship, int startX, int startY, int endX, int endY, int clearance)
            {
                Ship = ship;
                StartX = startX;
                StartY = startY; 
                EndX = endX; 
                EndY = endY;
                Clearance = clearance;
            }
        }
        public async Task BTFindPath(int threadIndex)
        {
            await Task.Run(() =>
            {
                //Debug.Log($"_Started BT #{threadIndex}:{Ships[threadIndex].Name} at {StartNodes[threadIndex].Vector} to {EndNodes[threadIndex].Vector} (Level coords)"); 
                //minimumClearance = 1;
                //maximumClearance = 1;
                //Debug.Log($"Trying to find a path for #{index} from ({StartNodes[index].x}, {StartNodes[index].y}) to ({EndNodes[index].x}, {EndNodes[index].y})");
                Totals[threadIndex] = SW.Stopwatch.StartNew();
                NeighborLoops[threadIndex] = SW.Stopwatch.StartNew();
                GetNodes[threadIndex] = SW.Stopwatch.StartNew();


                int BTLoops = 0;
                int BTTempCostToHere;


                for (int x = 0; x < _grid.Width; x++)
                {
                    for (int y = 0; y < _grid.Height; y++)
                    {
                        GridNodes[threadIndex][x][y].CostToHere = int.MaxValue;
                        GridNodes[threadIndex][x][y].TotalCost = int.MaxValue;
                        GridNodes[threadIndex][x][y].PreviousNode = MapNode.NullNode;

                        GridNodes[threadIndex][x][y].HasBeenChecked = false;
                        GridNodes[threadIndex][x][y].IsPartOfPath = false;

                    }
                }


                //Debug.Log($"Finished grid loops BT for #{threadIndex}:{Ships[threadIndex].Name}");


                //Debug.Log($"BTS: {StartNodes[index]}");
                //Debug.Log($"BTE: {EndNodes[index]}");

                //OrderPrintDebugImage(index);
                //return;
                Ships[threadIndex].DebugOriginalEndNode = EndNodes[threadIndex];
                Ships[threadIndex].DebugOriginalStartNode = StartNodes[threadIndex];
                if (EndNodes[threadIndex].Clearance < Clearances[threadIndex])
                {
                    //Debug.Log($"The end ({EndNodes[threadIndex].Vector}) for #{threadIndex}:{Ships[threadIndex].Name} isn't walkable space");
                    EndNodes[threadIndex] = FindNearestWalkablePoint(EndNodes[threadIndex], StartNodes[threadIndex], Clearances[threadIndex], threadIndex);
                    //Debug.Log($"Found new end point that is walkable for #{threadIndex}:{Ships[threadIndex].Name}: {EndNodes[threadIndex].Vector}");
                }
                //Debug.Log($"Found end point for #{threadIndex}:{Ships[threadIndex].Name}");
                if (StartNodes[threadIndex].Clearance < Clearances[threadIndex])
                {
                    //int startNodeLoops = 0;
                    //Stack<Vector2> pastLocations = new Stack<Vector2>(Ships[index].PastLocations.Reverse());
                    //while (StartNodes[index].Clearance < Clearances[index] && pastLocations.Count > 0)
                    //{
                    //    Debug.Log($"[Location History] {startNodeLoops}: for #{index}:{Ships[index].Name} StartNode at {StartNodes[index].Vector} doesn't have enough clearance, looking through past locations for a good start node");
                    //    Vector2Int pastLocation = Level.Pathfinder.ConvertToMapCoordinates(pastLocations.Pop());
                    //    StartNodes[index] = GridNodes[index][pastLocation.x][pastLocation.y];
                    //    startNodeLoops++;

                    //}
                    //if (pastLocations.Count == 0)
                    //{
                    //    Debug.Log($"[Location History] Ran out of past locations for #{index}:{Ships[index].Name} at {StartNodes[index].Vector}");

                    //}
                    //else
                    //{
                    //    Debug.Log($"[Location History] Found a valid start point for #{index}:{Ships[index].Name} at {StartNodes[index].Vector}");

                    //}
                    //Debug.Log($"The start ({StartNodes[threadIndex].Vector}) for #{threadIndex}:{Ships[threadIndex].Name} isn't walkable space");
                    StartNodes[threadIndex] = FindNearestWalkablePoint(StartNodes[threadIndex], EndNodes[threadIndex], Clearances[threadIndex], threadIndex);
                    //Debug.Log($"Found new start point that is walkable for #{threadIndex}:{Ships[threadIndex].Name}: {StartNodes[threadIndex].Vector}");
                }
                
                //Debug.Log($"Starting at {StartNodes[threadIndex].Index} for #{threadIndex}:{Ships[threadIndex].Name}");
                Path BTPath = new Path(StartNodes[threadIndex].x, StartNodes[threadIndex].y, EndNodes[threadIndex].x, EndNodes[threadIndex].y);

                List<MapNode> BTUncheckedNodes = new List<MapNode>() { StartNodes[threadIndex] };
                HashSet<MapNode> BTUncheckedNodesSet = new HashSet<MapNode> { StartNodes[threadIndex] };
                //SortedNodes = new SortedDictionary<int, MapNode> { { startNode.SortingId, startNode }  };
                HashSet<MapNode> BTCheckedNodes = new HashSet<MapNode>();

                //Debug.Log($"Initialized vars BT #{index}");

                StartNodes[threadIndex].CostToHere = 0;
                StartNodes[threadIndex].HueristicCost = MapNode.CalculateDistance(StartNodes[threadIndex], EndNodes[threadIndex]);
                StartNodes[threadIndex].CalculateTotalCost();
                //Debug.Log($"Initialized startnode BT #{index}");
                //if (startNode.ContainerNode != startNode)
                //{
                //    startNode.Neighbors.Add(startNode.ContainerNode);
                //}

                //loops = 0;
                MapNode BTPreviousNode = StartNodes[threadIndex];
                //Debug.Log($"Initialized previous BT #{index}");
                double BTStartupTime = Totals[threadIndex].Elapsed.TotalMilliseconds;
                //Debug.Log($"Startup time took: {BTStartupTime} ms");
                //Debug.Log($"Startnode: {startNode}, queueLoops: {queueLoops}, clearanceMapList: {_grid.ClearanceMapList.Count}");
                MapNode BTCurrentNode = MapNode.NullNode;
                //Debug.Log($"Starting at while loop for #{threadIndex}:{Ships[threadIndex].Name}");
                while (BTUncheckedNodes.Count > 0 && Totals[threadIndex].Elapsed.TotalSeconds < TimeLimit)
                {
                    //Debug.Log($"Inside while loop for #{threadIndex}:{Ships[threadIndex].Name} and have taken {Totals[threadIndex].Elapsed.Milliseconds}ms");
                    GetNodes[threadIndex].Start();
                    BTLoops++;
                    //if (BTLoops % 100 == 0)
                    //{
                    //    Debug.Log($"Loop #{BTLoops}");
                    //}

                    BTCurrentNode = GetCheapestNode(BTUncheckedNodes, BTPreviousNode);

                    BTPreviousNode = BTCurrentNode;

                    //Debug.Log($"Starting end check for #{threadIndex}:{Ships[threadIndex].Name}");
                    if (BTCurrentNode == EndNodes[threadIndex])
                    {
                        //Debug.Log($"Finished background finding path for #{threadIndex}:{Ships[threadIndex].Name}.");
                        MakeDestinationList(EndNodes[threadIndex], BTPath);
                        //Totals[threadIndex].Stop();
                        GetNodes[threadIndex].Stop();
                        //Debug.Log($"End point has been found for #{threadIndex}:{Ships[threadIndex].Name}");
                        //Debug.Log($"Finished background finding path and destination list for #{threadIndex}:{Ships[threadIndex].Name} in {Totals[threadIndex].Elapsed.TotalMilliseconds}ms");

                        Ships[threadIndex].PathfindingValue = BTPath;

                        Ships[threadIndex].PathfindingThreadComplete = true;
                        IsThreadActive[threadIndex] = false; //[alert] must be uncommented when not testing
                        return;
                    }
                    BTUncheckedNodes.Remove(BTCurrentNode);
                    BTUncheckedNodesSet.Remove(BTCurrentNode);

                    BTCheckedNodes.Add(BTCurrentNode);
                    BTCurrentNode.HasBeenChecked = true;

                    //Debug.Log($"Getting neighbors for {currentNode}");
                    //Debug.Log($"Starting at neighbor loop for #{threadIndex}:{Ships[threadIndex].Name}");
                    GetNodes[threadIndex].Stop();
                    NeighborLoops[threadIndex].Start();

                    BTCurrentNode.Neighbors.ForEach((neighbor) =>
                    {
                        if (!BTCheckedNodes.Contains(neighbor))
                        {
                            //Debug.Log($"Neighbor: {neighbor}");
                            if (neighbor.Clearance >= Clearances[threadIndex]) // < maximum clearance                 == 0
                            {
                                //Debug.Log($"Passed clearance");
                                BTTempCostToHere = BTCurrentNode.CostToHere + MapNode.CalculateDistance(BTCurrentNode, neighbor);
                                if (BTTempCostToHere < neighbor.CostToHere)
                                {
                                    //Debug.Log($"Lower Cost");
                                    neighbor.PreviousNode = BTCurrentNode;
                                    neighbor.CostToHere = BTTempCostToHere;
                                    neighbor.HueristicCost = MapNode.CalculateDistance(neighbor, EndNodes[threadIndex]);
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

                    NeighborLoops[threadIndex].Stop();

                }

                if (Totals[threadIndex].Elapsed.TotalSeconds > TimeLimit)
                {
                    //Debug.Log($"Ran out of time while trying to find a path #{threadIndex}");
                }
                else if (BTUncheckedNodes.Count == 0)
                {
                    //Debug.Log($"No more nodes to check for ship: {Ships[threadIndex].Name} Thread: #{threadIndex} Clearance: {Clearances[threadIndex]}.  checkedNodes: {BTCheckedNodes.Count} / {_grid.TotalNodes}  CurrentNode: {BTCurrentNode},");
                }
                Ships[threadIndex].PathfindingThreadComplete = true;
                IsThreadActive[threadIndex] = false; //[alert] must be uncommented when not testing
                Ships[threadIndex].PathfindingValue = null;


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
                    if (Level.ActivateCollisionAsteroids)
                    {
                        //UpdateMapTime[threadIndex] = SW.Stopwatch.StartNew();
                        UpdateMap(threadIndex, ship);
                        //UpdateMapTime[threadIndex].Stop();
                    }
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
                    PathsWaiting.Enqueue(new PathWaiting(ship, startX, startY, endX, endY, maximumClearance));
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

