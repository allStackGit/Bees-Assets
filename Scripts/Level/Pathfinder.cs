using Assets.Scripts.Entities;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Level
{
    public class Pathfinder

    {

        /// <summary>
        /// A boolean array of all integer coordinates on the map. False = valid space. True = obstacle
        /// </summary>
        public bool[][] Map;
        /// <summary>
        /// How much scaled down the pathfinding map is compared to the real map. Smaller size increases speed but decreases precision. Obstacles must be 
        /// at least as large on both axis as this number
        /// </summary>
        private int Scale = 1;
        public int Width, Height, HalfWidth, HalfHeight;
        public int ObstacleCount;
        public LevelStage Level;
        public List<Obstacle> Obstacles = new List<Obstacle>();
        /// <summary>
        /// A list of indexes of obstacles that need to be updated next time the map updates
        /// </summary>
        public List<int> ObstaclesToUpdate = new List<int>();
        public List<int[][]> ObstaclePoints = new List<int[][]>();
        public bool NeedsToBeUpdated;
        public bool HasMovingObstacles;

        public Pathfinder(LevelStage level)
        {
            Level = level;

            Width = Level.MapWidth / Scale;
            Height = Level.MapHeight / Scale;
            HalfWidth = Level.HalfMapWidth / Scale;
            HalfHeight = Level.HalfMapHeight / Scale;

            InitializeMap();
        }


        private void InitializeMap()
        {
            float start = Time.realtimeSinceStartup;
            Debug.Log($"Loading pathfinder map at {Scale}x");

            // initialize everything as open space
            Map = new bool[Width][];

            for (int x = 0; x < Map.Length; x++)
            {
                Map[x] = new bool[Height]; // [alert] only works for rectanglular maps, default of false means walkable
            }

            GameObject[] obstacles = GameObject.FindGameObjectsWithTag("Obstacle");

            foreach (GameObject obstacleObject in obstacles)
            {
                Obstacle obstacle = obstacleObject.GetComponent<Obstacle>();
                CollisionAsteroid collisionAsteroid = obstacleObject.GetComponent<CollisionAsteroid>();

                Debug.Log($"Found {obstacleObject.name}: {obstacle}");

                if ( collisionAsteroid != null )
                {
                    collisionAsteroid.Setup(Level, ObstacleCount++, new Vector2(-200, -200));
                }
                else
                {
                    obstacle.Setup(Level, ObstacleCount++);
                }
                AddObstacle(obstacle);

                Collider2D collider = obstacleObject.GetComponent<Collider2D>();
                Vector2 position = obstacleObject.transform.position;
                Vector2Int intPosition = new Vector2Int(Convert.ToInt32(position.x), Convert.ToInt32(position.y));

                int width = Convert.ToInt32(obstacleObject.transform.localScale.x);
                int height = Convert.ToInt32(obstacleObject.transform.localScale.y);

                int startX = Convert.ToInt32(position.x - (width / 2));
                int startY = Convert.ToInt32(position.y + (height / 2));

                bool isPolygon = false;
                bool isRotated = false;
                float rotation = obstacleObject.transform.localEulerAngles.z * Mathf.Deg2Rad;

                if (collider is PolygonCollider2D)
                {
                    Bounds bounds = collider.bounds;

                    width = (int)bounds.size.x;
                    height = (int)bounds.size.y;
                    startX = Convert.ToInt32(position.x - (width / 2));
                    startY = Convert.ToInt32(position.y + (height / 2));
                    //Debug.Log($"{collider.transform.name} is a polygon collider with a width of {width} and a height of {height} and a rotation of {rotation} that starts at {startX}, {startY}");
                    isPolygon = true;
                }
                if (rotation != 0 && !isPolygon)
                {
                    //Debug.Log($"{obstacleObject.gameObject.name} is rotated by {obstacleObject.transform.localEulerAngles.z} degrees / {rotation} radians");
                    isRotated = true;
                }

                List<int[]> points = new List<int[]>();
                for (int y = startY; y > startY - height; y -= Scale) // go across the bounds top to bottom (decreasing)
                {
                    for (int x = startX; x < startX + width; x += Scale) // go across the bounds, left to right (increasing)
                    {
                        Vector2Int point = new Vector2Int(x, y);
                        if (!isPolygon || collider.OverlapPoint(point))
                        {

                            if (isRotated)
                            {
                                Vector2Int rotatedPoint = Utilities.RotateIntPointAroundPoint(intPosition, point, rotation);
                                //Debug.Log($"#{i} Rotated {point} around {intPosition} to {rotatedPoint}");
                                point = rotatedPoint;
                            }
                            Vector2Int converted = ConvertToMapCoordinates(point);
                            //Debug.Log($"#{i} Converted {point} on the Map to (scaled) {converted} on the PathfindingMap");


                            Map[converted.y][converted.x] = true; // [note] I haven't figured out why we need to pass the Y to the X and the X to the Y but this works
                            points.Add(new int[] {converted.y, converted.x });
                        }
                    }
                }

                ObstaclePoints.Add(points.ToArray());



                //Debug.Log($"{obstacle.name} is located at {obstacle.transform.position} with a bounds of {bounds}, a width of {width} and a height of {height}");

            }
            //Utilities.Print2DArray(PathfindingMap);
            NeedsToBeUpdated = false;
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
            Debug.Log($"Setting #{id} to be updated");
        }
        public void AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
            if (obstacle.IsMobile && !HasMovingObstacles)
            {
                HasMovingObstacles = true;
            }
            NeedsToBeUpdated = true;
            //Debug.Log($"Adding {obstacle.Name} to Map");
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
        public void UpdateMap(List<Obstacle> obstacles)
        {
            float start = Time.realtimeSinceStartup;
            Debug.Log($"Updating map");

            if (obstacles.Count > 0) // there are obstacles within range of the ship that asked for the map to be updated
            {

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
                            Map[point[0]][point[1]] = false; // set its old position to walkable space
                        }

                        toRemove.Add(i);
                    }
                }
                toRemove.ForEach((index) =>
                {
                    ObstaclesToUpdate.RemoveAt(index);
                });

                if (ObstaclesToUpdate.Count == 0)
                {
                    NeedsToBeUpdated = false; 
                }
            }

            float end = (Time.realtimeSinceStartup - start) * 1000; // seconds to milliseconds
            Debug.Log($"InitializeMap() took {end} ms to complete.");

        }





        public Vector2Int ConvertToMapCoordinates(Vector2 coords)
        {
            return new Vector2Int(Convert.ToInt32(Width - (HalfWidth - coords.x)), Convert.ToInt32(Height - (HalfHeight + coords.y))) / Scale;
        }
        public Vector2 ConvertToLevelCoordinates(Vector2Int coords)
        {
            return new Vector2(-HalfWidth + coords.x, HalfHeight - coords.y) * Scale;
        }

    }
}