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
        public float TimeLimit = .25f;
        public float BTTimeLimit = 5;
        public int DebugLoops = 0;
        public int MaxLoopsPerFrame = 1000;


        // Threaded pathfinding work


        private Grid _grid;

        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number and even then, sometimes rotated obstacles aren't detected correctly
        /// </summary>

        private int _scale = 4;
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
        public void Update()
        {
            if (PathsWaiting.Count > 0)
            {
                PathsWaiting.ForEach((p) =>
                {
                    for (int i = 0; i < ConfigData.MaxThreads; i++)
                    {
                        if (!IsThreadActive[i])
                        {
                            IsThreadActive[i] = true;
                            Task task = new Task(() =>
                            {
                                StartNodes[i] = p.Start;
                                EndNodes[i] = p.End;
                                Clearances[i] = p.Clearance;
                                Ships[i] = p.Ship;
                                BTFindPath(i);
                            });
                            //Debug.Log($"Queued Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{i}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
                            //Debug.Log($"Queued Started BT #{i}");
                            task.Start();
                            //ThreadsStarted++;
                            PathsWaitingToRemove.Add(p);
                            break;
                        }
                    }
                    
                });
                PathsWaitingToRemove.ForEach((p) =>
                {
                    PathsWaiting.Remove(p);
                });
                PathsWaitingToRemove.Clear();
            }
            
        }
        private IEnumerator BakeMap()
        {
            float start = Time.realtimeSinceStartup;
            HashSet<MapNode> allNodes = new HashSet<MapNode>(_grid.ClearanceMap);
            Queue<MapNode> queue = new Queue<MapNode>();
            HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
            queue.Enqueue(_grid.ClearanceMapList.First());
            checkedNodes.Add(_grid.ClearanceMapList.First());
            while (queue.Count > 0)
            {
                MapNode node = queue.Dequeue();
                allNodes.UnionWith(node.Neighbors);
                foreach (MapNode neighbor in node.Neighbors)
                {
                    if (!checkedNodes.Contains(neighbor))
                    {
                        checkedNodes.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }

            List<MapNode> nodes = allNodes.Where((n) => n.Clearance > 1).ToList();
            long loops = 0;
            MapNode startNode;
            MapNode endNode;
            Debug.Log($"There are {nodes.Count} nodes");
            float end;
            for (int i = 0; i < nodes.Count; i++)
            {
                startNode = nodes[i];
                for (int j = 0; j < nodes.Count; j++)
                {
                    endNode = nodes[j];
                    if (startNode != endNode)
                    {
                        Level.StartCoroutine(FindPath(null, startNode.x, startNode.y, endNode.x, endNode.y, 1, null));
                    }
                    loops++;
                    
                }
                end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
                Debug.Log($"#{i} BakeMap() took {end} ms so far.");
                yield return ConfigData.WaitForEndOfFrame;
            }
            end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"BakeMap() took {end} ms to complete with {PathCache.Count} paths cached.");
            Level.StartCoroutine(SavePathCache());
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

        public IEnumerator CalculateSquares()
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
            HashSet<MapNode> potentialChildren = new HashSet<MapNode>();
            List<MapNode> potentialNodes;
            List<MapNode> largestNodes;
            List<MapNode> intersectingNodes = new List<MapNode>();
            //uncheckedNodes.Enqueue(_grid.GetNode(0, 0));
            int fullLoops = 0;
            int totalLargeNodes = 0;
            int clearance;
            while (checkedNodes.Count < _grid.NodeSet.Count)
            {
                float loopStart = Time.realtimeSinceStartup;
                fullLoops++;
                for (int y = 0; y < _grid.Height; y++)
                {
                    for (int x = 0; x < _grid.Width; x++)
                    {
                        currentNode = _grid.Nodes[x][y];
                        clearance = 1;

                        if (!checkedNodes.Contains(currentNode)) // skip obstacles and permanant nodes
                        {
                            hasHitObstacle = false;
                            minY = currentNode.y - clearance;
                            minX = currentNode.x - clearance;
                            maxY = currentNode.y + clearance;
                            maxX = currentNode.x + clearance;
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
                                                clearance++;
                                                maxY++;
                                                maxX++;
                                                minY--;
                                                minX--;
                                            }
                                        }
                                    }
                                }
                            }
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
                potentialNodes = _grid.NodeSet.Where((n) => !n.IsPermanant && n.Children.Count > 1 && n.ContainerNode == n).ToList();
                if (potentialNodes.Count > 0)
                {
                    largestNodeSize = potentialNodes.OrderByDescending(n => n.Children.Count).First().Children.Count;
                    largestNodes = potentialNodes.Where((n) => n.Children.Count == largestNodeSize).ToList();

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
                        if (!node.IsPermanant)
                        {
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
                        if (!node.IsPermanant)
                        {
                            node.IsPermanant = true;
                            node.ContainerNode = node;
                            _grid.ClearanceMap.Add(node);
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
            _grid.PrintPermanantNodesImage();
            //SaveClearanceMap("_b");

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"InitializeMap() took {end} ms to complete. There were {totalLoopCount} loops measuring square sizes");
        }

        public IEnumerator CalculateClearance()
        {
            float start = Time.realtimeSinceStartup;
            int totalLoopCount = 0;
            int minY, minX, maxY, maxX, boundsX, boundsY = 0;

            bool hasHitObstacle;
            MapNode currentNode;
            MapNode loopNode;

            for (int y = 0; y < _grid.Height; y++)
            {
                for (int x = 0; x < _grid.Width; x++)
                {
                    currentNode = _grid.Nodes[x][y];
                    if (currentNode.Clearance > 0)
                    {
                        hasHitObstacle = false;
                        minY = currentNode.y - currentNode.Clearance;
                        minX = currentNode.x - currentNode.Clearance;
                        maxY = currentNode.y + currentNode.Clearance;
                        maxX = currentNode.x + currentNode.Clearance;
                        while (!hasHitObstacle && maxX < _grid.Width && maxY < _grid.Height && minX >= 0 && minY >= 0)
                        {
                            // bottom border
                            //Debug.Log($"Checking clearance ({currentNode.Clearance+1}) for {currentNode.Index}: minX: {minX}, maxX: {maxX}, minY: {minY}, maxY: {maxY}");
                            for (boundsX = minX; boundsX <= maxX; boundsX++)
                            {
                                totalLoopCount++;
                                //loopNode = _grid.GetNode(boundsX, maxY);
                                loopNode = _grid.Nodes[boundsX][maxY];

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
                                    totalLoopCount++;
                                    //loopNode = _grid.GetNode(boundsX, minY);
                                    loopNode = _grid.Nodes[boundsX][minY];
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
                                        totalLoopCount++;
                                        //loopNode = _grid.GetNode(maxX, boundsY);
                                        loopNode = _grid.Nodes[maxX][boundsY];
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
                                            totalLoopCount++;
                                            //loopNode = _grid.GetNode(minX, boundsY);
                                            loopNode = _grid.Nodes[minX][boundsY];
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
                                            currentNode.Clearance += _scale;
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
                    }
                }
                yield return ConfigData.WaitForEndOfFrame;
            }
            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"InitializeMap() took {end} ms to complete. There were {totalLoopCount} loops measuring clearance");
            Level.StartCoroutine(CalculateSquares());
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
        public IEnumerator SavePathCache(string version = "")
        {
            float start = Time.realtimeSinceStartup;
            float end;


            //string json = "[";
            ////PathCache.ToList().ForEach((path) => json += $"{path.ToJson()}, ");
            //Debug.Log($"About to convert the paths to JSON list");
            //yield return ConfigData.WaitForEndOfFrame;
            //json += PathCache.Select((path) => $"{path.ToJson()}, ").Aggregate("", (agg, b) =>  agg + b);
            //Debug.Log($"Converted the paths to JSON list");
            //yield return ConfigData.WaitForEndOfFrame;
            //json = json.Remove(json.Length - 2);
            //json += "]";

            //string path = $"{ConfigData.GetBasePath()}/PathCache{version}.json";
            //Debug.Log($"Prepared to write");
            //yield return ConfigData.WaitForEndOfFrame;
            //File.WriteAllText(path, json);
            //end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            //Debug.Log($"Save Path Cache took {end} ms.");

            string path = $"{ConfigData.GetBasePath()}/PathCache{version}.txt";
            //Debug.Log($"Made the file path");
            //yield return ConfigData.WaitForEndOfFrame;
            StreamWriter writer = new StreamWriter(path, false);
            //Debug.Log($"Started the writer");
            //yield return ConfigData.WaitForEndOfFrame;
            //writer.WriteLine("[");
            //Debug.Log($"Wrote the opening bracket");
            //yield return ConfigData.WaitForEndOfFrame;
            List<string> pathsStrings = PathCache.Select((path) => $"{path.ToFile()}|").ToList();
            Debug.Log($"Converted the paths to File");
            yield return ConfigData.WaitForEndOfFrame;
            for (int i = 0; i < pathsStrings.Count + 4; i+=5)
            {
                //if (i < PathCache.Count - 5)
                //{
                //    writer.WriteLineAsync(pathsJson.Take(5).Aggregate("[", (agg, b) => agg + b + ",")+"]");
                //    if (i % 500 == 0)
                //    {
                //        Debug.Log($"Wrote line #{i} to disk for path cache");
                //        yield return ConfigData.WaitForEndOfFrame;
                //    }

                //}
                //else
                //{
                //    string json = pathsJson.ElementAt(i);
                //    Debug.Log($"Final json {json}");
                //    json = json.Remove(json.Length - 2);
                //    writer.WriteLine(json);
                //}
                string line = pathsStrings.Take(5).Aggregate("", (agg, b) => agg + b);
                //Debug.Log(pathsJson.Count);
                pathsStrings.RemoveRange(0, Math.Min(5, pathsStrings.Count));
                line = line.Remove(line.Length - 1);
                writer.WriteLine(line);
                if (i % 500 == 0)
                {
                    Debug.Log($"Wrote line #{i} to disk for path cache");
                    yield return ConfigData.WaitForEndOfFrame;
                }
                //Debug.Log($"Wrote line #{i} to disk for path cache");
                //yield return ConfigData.WaitForEndOfFrame;
            }
            //writer.WriteLine("]");
            writer.Close();
            end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"Save Path Cache took {end} ms.");
        }
        public IEnumerator LoadClearanceMap(Action callback)
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
            List<dynamic> nodes = Utilities.JArrayToList((JArray)JsonConvert.DeserializeObject(contents));

            foreach (dynamic node in nodes)
            {
                loops++;
                MapNode mapNode = _grid.Nodes[(int)node.x][(int)node.y];
                
                //mapNode.ContainerNode = _grid.Nodes[(int)node.CN.x][(int)node.CN.y];
                mapNode.ContainerNode = mapNode;
                mapNode.Clearance = (int)node.OC;
                mapNode.OriginalClearance = mapNode.Clearance;

                List<dynamic> children = Utilities.JArrayToList(node.C);
                List<dynamic> neighbors = Utilities.JArrayToList(node.N);
                //List<dynamic> immediateNeighbors = Utilities.JArrayToList(node.IN);

                children.ForEach((child) =>
                {
                    MapNode mapChild = _grid.Nodes[(int)child.x][(int)child.y];
                    mapChild.ContainerNode = mapNode;
                    mapChild.IsPermanant = true;
                    mapChild.OriginalClearance = (int) child.OC;
                    mapChild.Clearance = mapChild.OriginalClearance;
                    mapNode.Children.Add(mapChild);
                });

                neighbors.ForEach((neighbor) =>
                {
                    MapNode mapNeighbor = _grid.Nodes[(int)neighbor.x][(int)neighbor.y];
                    mapNeighbor.IsPermanant = true;
                    mapNode.Neighbors.Add(mapNeighbor);
                });

                //immediateNeighbors.ForEach((neighbor) =>
                //{
                //    MapNode mapNeighbor = _grid.Nodes[(int)neighbor.x][(int)neighbor.y];
                //    mapNeighbor.IsPermanant = true;
                //    mapNode.ImmediateNeighbors.Add(mapNeighbor);
                //});

                mapNode.IsPermanant = true;
                _grid.ClearanceMap.Add(mapNode);
                //Debug.Log($"Loaded {mapNode}");
                if (loops % 100 == 0)
                {
                    yield return ConfigData.WaitForEndOfFrame;
                }
            }
            //_grid.PrintPermanantNodesImage();
            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"Loaded clearance map in {end} ms");
            callback();
        }
        public IEnumerator LoadPathCache()
        {
            float fullStart = Time.realtimeSinceStartup;
            List<int> points;
            Path cachedPath;
            string[] paths;

            int lineCount = 0;

            foreach (string p in File.ReadAllLines($"{ConfigData.GetBasePath()}/PathCache.txt"))
            {
                lineCount++;
                //Debug.Log(p);
                paths = p.Split("|");
                foreach (string path in paths)
                {
                    //Debug.Log(path);
                    points = path.Split(",").Select(int.Parse).ToList();
                    cachedPath = new Path(points[0], points[1], points[2], points[3]);
                    cachedPath.Points = new List<Vector2>();

                    for (int  i = 4; i < points.Count; i += 2)
                    {
                        cachedPath.Points.Add(new Vector2(points[i], points[i+1]));
                    }

                    PathCache.Add(cachedPath);
                }

                if (lineCount % 500 == 0)
                {
                    yield return ConfigData.WaitForEndOfFrame;
                }

            }
            
            float end = (Time.realtimeSinceStartup - fullStart) * 1000; // seconds to milliseconds
            Debug.Log($"Loaded path cache in {end} ms");
            
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

            Level.StartCoroutine(LoadClearanceMap(() =>
            {
                _grid.ClearanceMapList = _grid.ClearanceMap.ToList();
                //Level.StartCoroutine(LoadPathCache());


                Queue<MapNode> queue = new Queue<MapNode>();
                HashSet<MapNode> checkedNodes = new HashSet<MapNode>();
                queue.Enqueue(_grid.ClearanceMapList[0]);
                checkedNodes.Add(_grid.ClearanceMapList[0]);
                while (queue.Count > 0)
                {
                    MapNode node = queue.Dequeue();
                    node.Neighbors.ForEach((neighbor) =>
                    {
                        if (!checkedNodes.Contains(neighbor))
                        {
                            checkedNodes.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    });

                }
                ResettableNodes = checkedNodes.ToList();


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
                            GridNodes[i][x][y].GetImmediateNeighbors(GridNodes[i]);
                        }
                    }
                }

                float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
                Debug.Log($"Initialized map in {end} ms");
                //Level.StartCoroutine(BakeMap());
            }));
            //Level.StartCoroutine(CalculateClearance());
            //Level.StartCoroutine(CalculateSquares());

        }
        /// <summary>
        /// adds the index to Obstacles to Update 
        /// </summary>
        /// <param name="id"></param>
        public void AddToUpdateList(int id)
        {
            ObstaclesToUpdate.Add(id);
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
                    //else
                    //{
                    //    Debug.Log($"{point} is in the bounds of {obstacle.Name} but does not overlap the collider");
                    //}
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
        public void UpdateMap(int thread, List<CollisionAsteroid> collisionAsteroids)
        {
            //float start = Time.realtimeSinceStartup;
            //Debug.Log($"Updating map");

            PreviousAsteroids[thread].ForEach((asteroidId) =>
            {
                //Debug.Log($"Clearing the position of {asteroid.Name} on the pathfinding map");
                foreach (int[] point in ObstaclePoints[asteroidId])
                {
                    if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                    {
                        GridNodes[thread][point[0]][point[1]].Clearance = GridNodes[thread][point[0]][point[1]].OriginalClearance; // set its old position to the original clearance
                    }
                }
            });

            PreviousAsteroids[thread].Clear();

            collisionAsteroids.ForEach((asteroid) =>
            {

                if (asteroid != null)
                {
                    // Get the new points
                    //Debug.Log($"Updating the position of {asteroid.Name} on the pathfinding map");
                    ObstaclePoints[asteroid.Id] = GetObstaclePoints(asteroid);
                    //Debug.Log($"Got obstacle points for {asteroid}");
                    foreach (int[] point in ObstaclePoints[asteroid.Id])
                    {
                        if (point[0] >= 0 && point[0] < _grid.Width && point[1] >= 0 && point[1] < _grid.Height)
                        {
                            //Debug.Log($"Valid indexes: {point[0]}, {point[1]}");
                            GridNodes[thread][point[0]][point[1]].Clearance = 0; // set its new position to unwalkable space
                        }
                    }
                    PreviousAsteroids[thread].Add(asteroid.Id);
                }
            });

        }
        float num, num2;
        private int CalculateDistance(MapNode a, MapNode b)
        {
            //int xDistance = Mathf.Abs(a.x - b.x);
            //int yDistance = Mathf.Abs(a.y - b.y);
            //int remaining = Mathf.Abs(xDistance - yDistance);
            //return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * remaining;

            //return (int) Vector2.Distance(a.Vector, b.Vector);

            num = a.VectorInt.x - b.VectorInt.x;
            num2 = a.VectorInt.y - b.VectorInt.y;
            return (int)Math.Sqrt(num * num + num2 * num2);
            //return (int) Math.Sqrt(Math.Pow((a.Vector.x -  b.Vector.x), 2) + Math.Pow((a.Vector.y - b.Vector.y), 2));

        }
        private MapNode cheapest;
        private int cheapestIterator;
        private MapNode GetCheapestNode(List<MapNode> list)
        {

            cheapest = list[0];
            for (cheapestIterator = 1; cheapestIterator < list.Count; cheapestIterator++)
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

        List<Vector2> destinationList;
        private void MakeDestinationList(MapNode endNode, Path path)
        {
            destinationList = new List<Vector2> { endNode.Vector };
            currentNode = endNode;
            //try
            //{
            //    while (currentNode.PreviousNode != MapNode.NullNode)
            //    {
            //        //Debug.Log(currentNode.PreviousNode.Id);
            //        destinationList.Add(currentNode.PreviousNode.Vector);
            //        currentNode.PreviousNode.IsPartOfPath = true;
            //        currentNode = currentNode.PreviousNode;
            //    }
            //}catch (Exception ex)
            //{
            //    Debug.Log(currentNode.PreviousNode);
            //    Debug.Log(currentNode.PreviousNode.Id);
            //    throw ex;
            //}

            while (currentNode.PreviousNode != MapNode.NullNode)
            {
                //Debug.Log(currentNode.PreviousNode.Id);
                destinationList.Add(currentNode.PreviousNode.Vector);
                //currentNode.PreviousNode.IsPartOfPath = true;
                currentNode = currentNode.PreviousNode;
            }

            destinationList.Reverse();
            //path.SetPoints(destinationList);
            path.Points = destinationList;
        }



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
        private int BTCalculateDistance(MapNode a, MapNode b)
        {
            int xDistance = Mathf.Abs(a.x - b.x);
            int yDistance = Mathf.Abs(a.y - b.y);
            return DIAGONAL_COST * Mathf.Min(xDistance, yDistance) + HORIZONTAL_COST * Mathf.Abs(xDistance - yDistance);

        }
        public MapNode BTFindNearestWalkablePoint(MapNode startNode, MapNode endNode, int minimumClearance, MapNode[][] nodes)
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
        private void BTMakeDestinationList(MapNode BTEndNode, Path BTPath)
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
        private MapNode BTGetCheapestNode(List<MapNode> list, MapNode previousNode)
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

        public List<PathWaiting> PathsWaiting = new List<PathWaiting>();
        public List<PathWaiting> PathsWaitingToRemove = new List<PathWaiting>();
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

            public PathWaiting(Ship ship, MapNode start, MapNode end, int clearance)
            {
                Ship = ship;
                Start = start;
                End = end;
                Clearance = clearance;
            }
        }
        public void BTFindPath(int index)
        {
            //Debug.Log($"_Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{index}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
            //Debug.Log($"_Started BT #{index}");
            //minimumClearance = 1;
            //maximumClearance = 1;
            //Debug.Log($"Trying to find a path with clearance ({minimumClearance} - {maximumClearance}) from ({startX}, {startY}) to ({endX}, {endY})");
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
            StartNodes[index] = GridNodes[index][StartNodes[index].x][StartNodes[index].y];
            EndNodes[index] = GridNodes[index][EndNodes[index].x][EndNodes[index].y];

            //Debug.Log($"BTS: {StartNodes[index]}");
            //Debug.Log($"BTE: {EndNodes[index]}");


            if (EndNodes[index].Clearance < Clearances[index])
            {
                //Debug.Log($"The end ({EndNodes[index]}) isn't walkable space");
                EndNodes[index] = BTFindNearestWalkablePoint(StartNodes[index], EndNodes[index], Clearances[index], GridNodes[index]);
                //Debug.Log($"Found new end point that is walkable: {EndNodes[index]}");
            }

            if (StartNodes[index].Clearance < Clearances[index])
            {
                //Debug.Log($"The start ({startNode.Vector}) isn't walkable space");
                StartNodes[index] = BTFindNearestWalkablePoint(EndNodes[index], StartNodes[index], Clearances[index], GridNodes[index]);
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
            StartNodes[index].HueristicCost = BTCalculateDistance(StartNodes[index], EndNodes[index]);
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
            while (BTUncheckedNodes.Count > 0 && GetNodes[index].Elapsed.TotalSeconds < BTTimeLimit)
            {
                GetNodes[index].Start();
                BTLoops++;
                //if (BTLoops % 100 == 0)
                //{
                //    Debug.Log($"Loop #{BTLoops}");
                //}

                BTCurrentNode = BTGetCheapestNode(BTUncheckedNodes, BTPreviousNode);

                BTPreviousNode = BTCurrentNode;

                if (BTCurrentNode == EndNodes[index])
                {
                    BTMakeDestinationList(EndNodes[index], BTPath);
                    Totals[index].Stop();
                    GetNodes[index].Stop();
                    //Debug.Log($"Finished background finding path #{index}. ({BTPath.Points.Count}) Loops: ({BTLoops}) startup time: {BTStartupTime}ms, getNode Time: {GetNodes[index].Elapsed.TotalMilliseconds}ms, " +
                    //    $"neighborLoop Time: {NeighborLoops[index].Elapsed.TotalMilliseconds}ms, Update Map Time: {UpdateMapTime[index].Elapsed.TotalMilliseconds}ms Total: {(Totals[index].Elapsed.TotalMilliseconds)}ms");
                    Ships[index].PathfindingValue = BTPath;
                    Ships[index].PathfindingThreadComplete = true;

                    Ships[index].DebugGrid = _grid;
                    Ships[index].DebugNodes = GridNodes[index];
                    Ships[index].DebugEndNode = EndNodes[index];
                    Ships[index].PrintDebugImage = false;
                    IsThreadActive[index] = false;
                    return;
                }
                BTUncheckedNodes.Remove(BTCurrentNode);
                BTUncheckedNodesSet.Remove(BTCurrentNode);

                BTCheckedNodes.Add(BTCurrentNode);
                BTCurrentNode.HasBeenChecked = true;

                //Debug.Log($"Getting neighbors for {currentNode}");
                GetNodes[index].Stop();
                NeighborLoops[index].Start();

                BTCurrentNode.ImmediateNeighbors.ForEach((neighbor) =>
                {
                    if (!BTCheckedNodes.Contains(neighbor))
                    {
                        //Debug.Log($"Neighbor: {neighbor}");
                        if (neighbor.Clearance >= Clearances[index]) // < maximum clearance                 == 0
                        {
                            //Debug.Log($"Passed clearance");
                            BTTempCostToHere = BTCurrentNode.CostToHere + BTCalculateDistance(BTCurrentNode, neighbor);
                            if (BTTempCostToHere < neighbor.CostToHere)
                            {
                                //Debug.Log($"Lower Cost");
                                neighbor.PreviousNode = BTCurrentNode;
                                neighbor.CostToHere = BTTempCostToHere;
                                neighbor.HueristicCost = BTCalculateDistance(neighbor, EndNodes[index]);
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


            if (GetNodes[index].Elapsed.TotalSeconds > BTTimeLimit)
            {
                Debug.Log($"Ran out of time while trying to find a path #{index}");
            }
            else if (BTUncheckedNodes.Count == 0)
            {
                Debug.Log($"No more nodes to check #{index} Clearance: {Clearances[index]}.  checkedNodes: {BTCheckedNodes.Count} / {_grid.TotalNodes}  CurrentNode: {BTCurrentNode},");
            }
            Ships[index].DebugGrid = _grid;
            Ships[index].DebugNodes = GridNodes[index];
            Ships[index].DebugEndNode = EndNodes[index];
            Ships[index].DebugStartNode = StartNodes[index];
            Ships[index].PathfindingThreadComplete = true;
            Ships[index].PrintDebugImage = true;
            IsThreadActive[index] = false;
        }

        private MapNode startNode, endNode, currentNode, previousNode;
        private Path path, potentialCachedPath, cachedPath;
        private float start, neighborLoop, getNode, startTimer, middleTimer, startupTime, sortTime, total;
        private int loops, tempCostToHere, iterator;
        public IEnumerator FindPath(Ship ship, int startX, int startY, int endX, int endY, int maximumClearance, Action<Path> callback)
        {

            //minimumClearance = 1;
            //maximumClearance = 1;
            //Debug.Log($"Trying to find a path with clearance ({minimumClearance} - {maximumClearance}) from ({startX}, {startY}) to ({endX}, {endY})");
            start = Time.realtimeSinceStartup;
            neighborLoop = 0;
            getNode = 0;

            startNode = _grid.Nodes[startX][startY];
            endNode = _grid.Nodes[endX][endY];


            //Totals[Thread] = SW.Stopwatch.StartNew();
            //ThreadsStarted++;
            //ThreadIndex = ThreadsStarted % ConfigData.MaxThreads;
            //PathsWaiting = PathsWaiting.Where((p) => p.Ship != ship).ToList(); // remove all queued pathfinding for this ship
            //if (!IsThreadActive[ThreadIndex])
            //{
            //    IsThreadActive[ThreadIndex] = true;
            //    Task task = new Task(() =>
            //    {
            //        StartNodes[ThreadIndex] = startNode;
            //        EndNodes[ThreadIndex] = endNode;
            //        Clearances[ThreadIndex] = maximumClearance;
            //        Ships[ThreadIndex] = ship;
            //        BTFindPath(ThreadIndex);
            //    });
            //    Debug.Log($"Standard Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{ThreadIndex}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
            //    task.Start();
            //}
            //else
            //{
            //    ThreadsStarted--;
            //    //Debug.Log($"Queued BT #{Thread} / {ThreadsStarted} because the thread is active");
            //    PathsWaiting.Add(new PathWaiting(ship, startNode, endNode, maximumClearance));
            //}
            bool startedTask = false;
            PathsWaiting = PathsWaiting.Where((p) => p.Ship != ship).ToList(); // remove all queued pathfinding for this ship
            for (int i = 0; i < ConfigData.MaxThreads; i++)
            {
                if (!IsThreadActive[i])
                {
                    if (Level.ActivateCollisionAsteroids)
                    {
                        UpdateMapTime[i] = SW.Stopwatch.StartNew();
                        //Debug.Log("Before updating map");
                        UpdateMap(i, ship.NearbyAsteroids.ToList());
                        //Debug.Log("After updating map");
                        UpdateMapTime[i].Stop();
                    }
                    IsThreadActive[i] = true;
                    Task task = new Task(() =>
                    {
                        StartNodes[i] = startNode;
                        EndNodes[i] = endNode;
                        Clearances[i] = maximumClearance;
                        Ships[i] = ship;
                        BTFindPath(i);
                    });
                    //Debug.Log($"Standard Started BT {ThreadsStarted} % {ConfigData.MaxThreads} : #{i}|{Thread}|{(ThreadsStarted % ConfigData.MaxThreads)} ");
                    //Debug.Log($"Standard Started #{i}");
                    task.Start();
                    startedTask = true;
                    //ThreadsStarted++;
                    //PathsWaitingToRemove.Add(p);
                    break;
                }
            }
            if (!startedTask)
            {
                PathsWaiting.Add(new PathWaiting(ship, startNode, endNode, maximumClearance));
            }

            //Debug.Log($"Finished starting path in {(Time.realtimeSinceStartup - start) * 1000} ms");

            callback(null);
            yield break;
            //if (ConfigData.UsedThreads < ConfigData.MaxThreads)
            //{
            //    Task task = new Task(() =>
            //    {
            //        BTFindPath(ship, startNode, endNode, maximumClearance);
            //    });
            //    task.Start();
            //    ConfigData.UsedThreads++;
            //    Debug.Log($"Started thread #{ConfigData.UsedThreads}");
            //}
            //else
            //{
            //    Debug.Log($"Maxed out threads: {ConfigData.UsedThreads}/{ConfigData.MaxThreads}");
            //}
            //Task task = new Task(() =>
            //{
            //    BTFindPath(ship, startNode, endNode, maximumClearance);
            //});
            //task.Start();


            if (endNode.ContainerNode.Clearance < maximumClearance)
            {
                endNode = FindNearestWalkablePoint(startNode, endNode, maximumClearance);
            }

            if (startNode.ContainerNode.Clearance < maximumClearance)
            {
                //Debug.Log($"The start ({startNode.Vector}) isn't walkable space");
                startNode = FindNearestWalkablePoint(endNode, startNode, maximumClearance);
                //Debug.Log($"Found new start point that is walkable: {startNode.Vector}");
            }


            path = new Path(startNode.x, startNode.y, endNode.x, endNode.y);

            UncheckedNodes = new List<MapNode>() { startNode };
            UncheckedNodesSet = new HashSet<MapNode> { startNode };
            //SortedNodes = new SortedDictionary<int, MapNode> { { startNode.SortingId, startNode }  };
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
            ResettableNodes.ForEach((n) =>
            {
                n.CostToHere = int.MaxValue;
                n.TotalCost = int.MaxValue;
                n.PreviousNode = MapNode.NullNode;
            });

            startNode.CostToHere = 0;
            startNode.HueristicCost = CalculateDistance(startNode, endNode);
            startNode.CalculateTotalCost();
            if (startNode.ContainerNode != startNode)
            {
                startNode.Neighbors.Add(startNode.ContainerNode);
            }

            //loops = 0;
            previousNode = startNode;
            startupTime = (Time.realtimeSinceStartup - start) * 1000;
            //Debug.Log($"Startup time took: {startupTime} ms");
            //Debug.Log($"Startnode: {startNode}, queueLoops: {queueLoops}, clearanceMapList: {_grid.ClearanceMapList.Count}");
            while (UncheckedNodes.Count > 0 && getNode < TimeLimit)
            {
                startTimer = Time.realtimeSinceStartup;
                //loops++;

                currentNode = GetCheapestNode(UncheckedNodes);
                //if (loops > 1)
                //{
                //    Debug.Log($"Difference in TC between current and previous: {currentNode.TotalCost - previousNode.TotalCost}");
                //}
                previousNode = currentNode;
                //currentNode = UncheckedNodes.First();
                //currentNode = SortedNodes.First().Value;
                //Debug.Log($"Sorted set, first: {SortedNodes.First().Value.TotalCost}, cheapestNode: {currentNode.TotalCost}");
                //yield return ConfigData.WaitForEndOfFrame;

                //currentNode = UncheckedNodes.First();
                //Debug.Log($"Current node: {currentNode}");

                // skips ahead to further down the line if it detects we're in free space
                //if (loops % 20 == 0 && (IsInFreeSpace(currentNode.x, currentNode.y, endNode.x, endNode.y)))
                //{
                //    endNode.PreviousNode = currentNode;
                //    MakeDestinationList(endNode, path);
                //    PathCache.Add(path);
                //    return path;
                //}

                //skip ahead to further down if we raycasted a straight line
                //if (loops % 20 == 0 && (CalculateDistance(currentNode, endNode) > 40 && !Utilities.HasObstaclesInTheWay(currentNode.Vector, endNode.Vector)))
                //{
                //    endNode.PreviousNode = currentNode;
                //    MakeDestinationList(endNode, path);
                //    PathCache.Add(path);
                //    Debug.Log($"Found a straight line from {currentNode.Vector} to the end {endNode.Vector}");
                //    _grid.DebugGridAsImage(new Vector2Int(endNode.x, endNode.y));
                //    callback(path);
                //    yield break;
                //}

                //if (currentNode == endNode)
                //{
                //    MakeDestinationList(endNode, path);
                //    //PathCache.Add(path);
                //    //Debug.Log($"Reached the end destination");
                //    //_grid.DebugGridAsImage(new Vector2Int(currentNode.x, currentNode.y));
                //    callback(path);
                //    yield break;
                //}
                //else 
                if (currentNode.Children.Contains(endNode) || currentNode == endNode)
                {
                    if (endNode != currentNode)
                    {
                        endNode.PreviousNode = currentNode;
                    }
                    MakeDestinationList(endNode, path);

                    PathCache.Add(path);
                    //Debug.Log($"Found the end node ({loops} loops) as a child of another node {currentNode}");
                    total = Time.realtimeSinceStartup - start;
                    Debug.Log($"Finished finding path. Loops: ({loops}) startup time: {startupTime}ms, getNode Time: {getNode * 1000}ms, neighborLoop Time: {neighborLoop * 1000}ms, Total: {(total * 1000)}ms");
                    //_grid.DebugGridAsImage(new Vector2Int(endNode.x, endNode.y), _grid.Nodes, 4);
                    //Debug.Log($" == {MapNode.equalsCalls}");
                    callback(path);
                    yield break;
                }
                UncheckedNodes.Remove(currentNode);
                UncheckedNodesSet.Remove(currentNode);
                //SortedNodes.Remove(currentNode.SortingId);
                currentNode.HasBeenChecked = true;
                CheckedNodes.Add(currentNode);

                //Debug.Log($"Getting neighbors for {currentNode}");
                getNode += Time.realtimeSinceStartup - startTimer;

                startTimer = Time.realtimeSinceStartup;

                currentNode.Neighbors.ForEach((neighbor) =>
                {
                    if (!CheckedNodes.Contains(neighbor))
                    {
                        if (neighbor.Clearance > maximumClearance) // < maximum clearance                 == 0
                        {
                            tempCostToHere = currentNode.CostToHere + CalculateDistance(currentNode, neighbor);
                            if (tempCostToHere < neighbor.CostToHere)
                            {
                                //SortedNodes.Remove(neighbor.SortingId);
                                neighbor.PreviousNode = currentNode;
                                neighbor.CostToHere = tempCostToHere;
                                neighbor.HueristicCost = CalculateDistance(neighbor, endNode);
                                neighbor.CalculateTotalCost();
                                //UncheckedNodes.Add(neighbor);
                                if (!UncheckedNodesSet.Contains(neighbor))
                                {
                                    UncheckedNodes.Add(neighbor);
                                    UncheckedNodesSet.Add(neighbor);
                                    //SortedNodes.Add(neighbor.SortingId, neighbor);
                                }
                            }
                        }
                    }
                });

                //if (loops % MaxLoopsPerFrame == 0)
                //{
                //    yield return ConfigData.WaitForEndOfFrame;
                //}
                neighborLoop += Time.realtimeSinceStartup - startTimer;

            }


            if (getNode > TimeLimit)
            {
                Debug.Log($"Ran out of time while trying to find a path");
            }
            else if (UncheckedNodes.Count == 0)
            {
                Debug.Log($"No more nodes to check.  checkedNodes: {CheckedNodes.Count} / {_grid.TotalNodes} / {_grid.ClearanceMap.Count}  CurrentNode: {currentNode},");
            }

            // couldn't find the path
            //_grid.DebugGridAsImage(new Vector2Int(currentNode.x, currentNode.y));
            //currentNode.DebugNodeImage();
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
        public Vector2Int ConvertToLevelCoordinatesInt(int x, int y)
        {
            return new Vector2Int(-HalfWidth + x, HalfHeight - y) * _scale;
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
            //public Dictionary<long, Path> EmptyPathsSet = new Dictionary<long, Path>();
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
            public MapNode GetNode(int x, int y)
            {
                //if (x == 487 && y == 66)
                //{
                //    Debug.Log($"Found ghost node! {Nodes[x][y]}");
                //}
                return Nodes[x][y];
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
                        Color color = Color.grey; // has not been checked
                        node = nodes[ x/ scale][y / scale];
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


                        for (int v = 0; v < scale; v++)
                        {
                            for (int h = 0; h < scale; h++)
                            {
                                texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color); // regular
                            }
                        }

                        //texture.SetPixel(x, Height * 2 - (y + 1), color); // regular
                        //texture.SetPixel(x + 1, Height * 2 - (y + 1), color); // right
                        //texture.SetPixel(x, Height * 2 - y, color); // down
                        //texture.SetPixel(x + 1, Height * 2 - y, color); // down and right
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((lastNode.x * scale) + h, (Height * scale) - ((lastNode.y * scale) + (1 - v)), ConfigData.GetUIColor("medium")); // last pixel
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((firstNode.x * scale) + h, (Height * scale) - ((firstNode.y * scale) + (1 - v)), ConfigData.GetUIColor("good")); // last pixel
                    }
                }

                //texture.SetPixel(lastNode.x, Height * 2 - (lastNode.y + 1), ConfigData.GetUIColor("medium"));
                //texture.SetPixel(lastNode.x + 1, Height * 2 - (lastNode.y + 1), ConfigData.GetUIColor("medium"));
                //texture.SetPixel(lastNode.x, Height * 2 - lastNode.y, ConfigData.GetUIColor("medium"));
                //texture.SetPixel(lastNode.x + 1, Height * 2 - lastNode.y, ConfigData.GetUIColor("medium"));
                //Debug.Log($"Setting last pixel at ({lastNode.x}, {(Height - (lastNode.y + 1))}) to yellow");
                //Color[] pixels = texture.GetPixels();
                //System.Array.Reverse(pixels, 0, pixels.Length);
                //texture.SetPixels(pixels);
                texture.Apply();
                string path = $"{ConfigData.GetBasePath()}/{ship.ShipType}_{ship.Id}_{Utilities.Hash()}.png";
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
            public bool IsPermanant;
            public MapNode PreviousNode = NullNode;
            public MapNode ContainerNode = NullNode;
            public HashSet<MapNode> Children = new HashSet<MapNode>();
            public List<MapNode> Neighbors = new List<MapNode>();
            public List<MapNode> ImmediateNeighbors = new List<MapNode>();
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
                Neighbors = Neighbors.ToHashSet().ToList();
                return Neighbors;
            }
            public List<MapNode> GetImmediateNeighbors(MapNode[][] nodes)
            {

                if (x - 1 >= 0) // There is space to the left
                {

                    ImmediateNeighbors.Add(nodes[x - 1][y]); // get left neighbor

                    if (y - 1 >= 0)
                    {
                        ImmediateNeighbors.Add(nodes[x - 1][y - 1]); // get bottom left neighbor
                    }

                    if (y + 1 < Grid.Height)
                    {
                        ImmediateNeighbors.Add(nodes[x - 1][y + 1]); // get top left neighbor
                    }

                }
                if (x + 1 < Grid.Width) // There is space to the right
                {

                    ImmediateNeighbors.Add(nodes[x + 1][y]); // get right neighbor

                    if (y - 1 >= 0)
                    {
                        ImmediateNeighbors.Add(nodes[x + 1][y - 1]); // get bottom right neighbor
                    }

                    if (y + 1 < Grid.Height)
                    {
                        ImmediateNeighbors.Add(nodes[x + 1][y + 1]); // get top right neighbor
                    }

                }

                if (y - 1 >= 0) // there is space below
                {
                    ImmediateNeighbors.Add(nodes[x][y - 1]);
                }

                if (y + 1 < Grid.Height) // there is space above
                {
                    ImmediateNeighbors.Add(nodes[x][y + 1]);
                }


                return ImmediateNeighbors;
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
                return $"MapNode #{Id} - {(IsPermanant ? "Y" : "N")}: ({x}, {y}), PreviousNode: {PreviousNode.Id}, Clearance: {Clearance}, Container: #{ContainerNode.Id}, IN: {ImmediateNeighbors.Count}\n";
            }
            public override string ToString()
            {
                return Info();
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

                toString += $"Immediate Neighbors ({ImmediateNeighbors.Count}):\n\n";
                for (int i = 0; i < ImmediateNeighbors.Count && i < 10; i++)
                {
                    toString += ImmediateNeighbors.ElementAt(i).Info();
                }
                return toString;

            }
            public string ToJson()
            {
                string json = $"{{\"Id\": {Id}, \"x\": {x}, \"y\": {y}, \"OC\": {OriginalClearance}, \"C\": [";
                Children.ToList().ForEach((n) => json += $" {{ \"Id\": {n.Id}, \"x\": {n.x}, \"y\": {n.y}, \"OC\": {n.OriginalClearance} }},");
                if (Children.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }

                json += "], \"N\": [";
                Neighbors.ForEach((n) => json += $" {{ \"Id\": {n.Id}, \"x\": {n.x}, \"y\": {n.y}, \"OC\": {n.OriginalClearance} }},");

                if (Neighbors.Count > 0)
                {
                    json = json.Substring(0, json.Length - 1);
                }

                //json += "], \"IN\": [";
                //ImmediateNeighbors.ForEach((n) => json += $" {{ \"Id\": {n.Id}, \"x\": {n.x}, \"y\": {n.y}, \"OC\": {n.OriginalClearance} }},");

                //if (ImmediateNeighbors.Count > 0)
                //{
                //    json = json.Substring(0, json.Length - 1);
                //}

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

