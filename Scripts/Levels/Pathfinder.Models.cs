using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
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
                MapNode node;
                for (int y = 0; y < Height * scale; y += scale)
                {
                    for (int x = 0; x < Width * scale; x += scale)
                    {
                        node = nodes[x / scale][y / scale];
                        float darkness = 5.0f;
                        Color color = new Color(node.Clearance / darkness, node.Clearance / darkness, node.Clearance / darkness);
                        if (node.Clearance >= ship.GetClearance())
                        {
                            color = Color.green;
                        }

                        if (node.Clearance == 0)
                        {
                            color = ConfigData.GetUIColor("bad");
                        }
                        else if (node.IsPartOfPath)
                        {
                            color = ConfigData.GetUIColor("medium");
                        }
                        else if (node.HasBeenChecked)
                        {
                            color = Color.cyan;
                        }

                        if (ship.DebugWalkablePointNodes.Contains(node))
                        {
                            color = new Color(.94f, .59f, .29f, 1);
                        }

                        for (int v = 0; v < scale; v++)
                        {
                            for (int h = 0; h < scale; h++)
                            {
                                texture.SetPixel((node.x * scale) + h, (Height * scale) - ((node.y * scale) + (1 - v)), color);
                            }
                        }
                    }
                }

                for (int v = 0; v < scale; v++)
                {
                    for (int h = 0; h < scale; h++)
                    {
                        texture.SetPixel((firstNode.x * scale) + h, (Height * scale) - ((firstNode.y * scale) + (1 - v)), Color.red);
                        texture.SetPixel((lastNode.x * scale) + h, (Height * scale) - ((lastNode.y * scale) + (1 - v)), Color.blue);
                        texture.SetPixel((ship.DebugOriginalStartNode.x * scale) + h, (Height * scale) - ((ship.DebugOriginalStartNode.y * scale) + (1 - v)), Color.magenta);
                        texture.SetPixel((ship.DebugOriginalEndNode.x * scale) + h, (Height * scale) - ((ship.DebugOriginalEndNode.y * scale) + (1 - v)), Color.white);
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
            public int CostToHere;
            public int HueristicCost;
            public int TotalCost;
            public int x, y;
            public Vector2Int Index;
            public readonly int Id;
            public int OriginalClearance;
            public int Clearance;
            public bool HasBeenChecked;
            public bool IsPartOfPath;
            public MapNode PreviousNode = NullNode;
            public List<MapNode> Neighbors;
            public Grid Grid;
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
                if (x - 1 >= 0)
                {
                    Neighbors.Add(nodes[x - 1][y]);
                    if (y - 1 >= 0)
                    {
                        Neighbors.Add(nodes[x - 1][y - 1]);
                    }
                    if (y + 1 < Grid.Height)
                    {
                        Neighbors.Add(nodes[x - 1][y + 1]);
                    }
                }
                if (x + 1 < Grid.Width)
                {
                    Neighbors.Add(nodes[x + 1][y]);
                    if (y - 1 >= 0)
                    {
                        Neighbors.Add(nodes[x + 1][y - 1]);
                    }
                    if (y + 1 < Grid.Height)
                    {
                        Neighbors.Add(nodes[x + 1][y + 1]);
                    }
                }
                if (y - 1 >= 0)
                {
                    Neighbors.Add(nodes[x][y - 1]);
                }
                if (y + 1 < Grid.Height)
                {
                    Neighbors.Add(nodes[x][y + 1]);
                }
                return Neighbors;
            }

            public void CalculateTotalCost()
            {
                TotalCost = CostToHere + HueristicCost;
            }

            public override bool Equals(object obj)
            {
                return this == ((MapNode)obj);
            }

            public static bool operator ==(MapNode a, MapNode b)
            {
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

            public int CompareTo(object other)
            {
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
            public List<Vector2> Points;
            public int StartX, StartY, EndX, EndY;
            public int EgressPointCount;
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

            public override bool Equals(object obj)
            {
                if (obj == null)
                {
                    return false;
                }
                Path p = obj as Path;
                return p != null && Id == p.Id;
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
                if (ReferenceEquals(a, b))
                {
                    return true;
                }
                if (((object)a == null) || ((object)b == null))
                {
                    return false;
                }
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
