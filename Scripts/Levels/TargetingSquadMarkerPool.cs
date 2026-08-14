using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Stage-owned pool and shared lifetime driver for transient targeting markers.
    /// Keeps marker GameObjects reusable and avoids one recurring Level timer per marker.
    /// </summary>
    public class TargetingSquadMarkerPool : MonoBehaviour
    {
        private Stage _stage;
        private readonly Stack<TargetingSquadMarker> _inactive = new Stack<TargetingSquadMarker>();
        private readonly HashSet<int> _inactiveIds = new HashSet<int>();
        private readonly List<TargetingSquadMarker> _active = new List<TargetingSquadMarker>();

        public static TargetingSquadMarkerPool GetOrCreate(Stage stage)
        {
            TargetingSquadMarkerPool pool = stage.GetComponent<TargetingSquadMarkerPool>();
            if (pool == null)
            {
                pool = stage.gameObject.AddComponent<TargetingSquadMarkerPool>();
            }
            pool._stage = stage;
            return pool;
        }

        public void Show(Ship enemyShip)
        {
            if (enemyShip == null)
            {
                return;
            }

            TargetingSquadMarker marker = null;
            while (_inactive.Count > 0 && marker == null)
            {
                marker = _inactive.Pop();
                if (marker != null)
                {
                    _inactiveIds.Remove(marker.GetEntityId());
                }
            }

            if (marker == null)
            {
                GameObject markerObject = Instantiate(_stage.Prefabs.TargetingSquadPrefab, transform);
                marker = markerObject.GetComponent<TargetingSquadMarker>();
                markerObject.SetActive(false);
            }

            marker.transform.SetParent(enemyShip.transform, false);
            marker.transform.localPosition = Vector2.zero;
            marker.gameObject.SetActive(true);
            marker.Setup(this, enemyShip);
            _active.Add(marker);
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                TargetingSquadMarker marker = _active[i];
                if (marker == null)
                {
                    _active.RemoveAt(i);
                    continue;
                }
                marker.Tick(deltaTime);
            }
        }

        public void Release(TargetingSquadMarker marker)
        {
            if (marker == null)
            {
                return;
            }

            _active.Remove(marker);
            if (!_inactiveIds.Add(marker.GetEntityId()))
            {
                return;
            }

            marker.ResetForPool();
            marker.gameObject.SetActive(false);
            marker.transform.SetParent(transform, false);
            _inactive.Push(marker);
        }
    }
}
