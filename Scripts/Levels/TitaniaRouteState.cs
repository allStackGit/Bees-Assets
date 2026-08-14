using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    internal static class TitaniaRouteState
    {
        private static readonly HashSet<Vector2Int> OpenedBarrierPositions = new HashSet<Vector2Int>();

        internal static void BeginMinesweeper()
        {
            OpenedBarrierPositions.Clear();
        }

        internal static void RecordOpenedBarrier(Vector2 localPosition)
        {
            OpenedBarrierPositions.Add(ToKey(localPosition));
        }

        internal static bool WasBarrierOpened(Vector2 localPosition)
        {
            return OpenedBarrierPositions.Contains(ToKey(localPosition));
        }

        private static Vector2Int ToKey(Vector2 position)
        {
            return new Vector2Int(
                Mathf.RoundToInt(position.x * 10f),
                Mathf.RoundToInt(position.y * 10f));
        }
    }
}
