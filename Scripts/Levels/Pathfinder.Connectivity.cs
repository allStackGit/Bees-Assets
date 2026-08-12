using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Pathfinder
    {
        private readonly Dictionary<int, int[]> _staticConnectivityByClearance = new Dictionary<int, int[]>();

        /// <summary>
        /// Returns whether two level-local positions belong to the same permanently walkable
        /// region for the requested clearance. Dynamic obstacles are deliberately ignored: this
        /// is an inexpensive rejection for authored walls/sealed pockets before launching A*.
        /// </summary>
        public bool AreStaticallyConnected(Vector2 start, Vector2 destination, int shipClearance)
        {
            if (!Level.HasObstacles)
            {
                return true;
            }

            // A pending static rebuild means the cached base map may be stale. Never reject a
            // route from stale connectivity data; normal pathfinding will decide it instead.
            if (_staticObstacleRebuildPending)
            {
                return true;
            }

            if (_staticObstacleLayerDirty)
            {
                UpdateDynamicObstacleLayer();
                _staticConnectivityByClearance.Clear();
            }

            int clearance = GetEffectivePathClearance(shipClearance);
            Vector2Int startCoordinates = ConvertToMapCoordinates(Level.ForceBounds(start));
            Vector2Int destinationCoordinates = ConvertToMapCoordinates(Level.ForceBounds(destination));
            int startIndex = ToIndex(startCoordinates.x, startCoordinates.y);
            int destinationIndex = ToIndex(destinationCoordinates.x, destinationCoordinates.y);

            if (_baseClearance[startIndex] < clearance || _baseClearance[destinationIndex] < clearance)
            {
                // Ships already intersecting authored geometry may still use the existing static
                // egress recovery path, so do not classify those cases as disconnected here.
                return true;
            }

            if (!_staticConnectivityByClearance.TryGetValue(clearance, out int[] components))
            {
                components = BuildStaticConnectivity(clearance);
                _staticConnectivityByClearance[clearance] = components;
            }

            int startComponent = components[startIndex];
            return startComponent != 0 && startComponent == components[destinationIndex];
        }

        private int[] BuildStaticConnectivity(int clearance)
        {
            int[] components = new int[_totalNodes];
            Queue<int> open = new Queue<int>();
            int component = 0;

            for (int index = 0; index < _totalNodes; index++)
            {
                if (components[index] != 0 || _baseClearance[index] < clearance)
                {
                    continue;
                }

                component++;
                components[index] = component;
                open.Enqueue(index);

                while (open.Count > 0)
                {
                    int currentIndex = open.Dequeue();
                    int currentX = ToX(currentIndex);
                    int currentY = ToY(currentIndex);

                    for (int neighbor = 0; neighbor < NeighborX.Length; neighbor++)
                    {
                        int neighborX = currentX + NeighborX[neighbor];
                        int neighborY = currentY + NeighborY[neighbor];
                        if (neighborX < 0 || neighborY < 0 || neighborX >= Width || neighborY >= Height)
                        {
                            continue;
                        }

                        int neighborIndex = ToIndex(neighborX, neighborY);
                        if (components[neighborIndex] != 0 || _baseClearance[neighborIndex] < clearance ||
                            IsDiagonalMoveBlocked(currentX, currentY, neighborX, neighborY, clearance, _baseClearance))
                        {
                            continue;
                        }

                        components[neighborIndex] = component;
                        open.Enqueue(neighborIndex);
                    }
                }
            }

            return components;
        }
    }
}
