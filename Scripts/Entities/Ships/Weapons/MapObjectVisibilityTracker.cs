using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// Runtime companion added only while a MapObject is visible to player range sources.
    /// It owns the set of observing ranges so one range exiting cannot hide an object
    /// still observed by another, and deactivation/destruction removes the object from GameState.
    /// </summary>
    public sealed class MapObjectVisibilityTracker : MonoBehaviour
    {
        private readonly HashSet<RangeCollider> _sources = new HashSet<RangeCollider>();
        private readonly List<MapObject> _visibleSurvivors = new List<MapObject>();
        private MapObject _mapObject;
        private GameState _state;

        public static MapObjectVisibilityTracker GetOrCreate(MapObject mapObject, GameState state)
        {
            if (mapObject == null || state == null)
            {
                return null;
            }

            MapObjectVisibilityTracker tracker = mapObject.GetComponent<MapObjectVisibilityTracker>();
            if (tracker == null)
            {
                tracker = mapObject.gameObject.AddComponent<MapObjectVisibilityTracker>();
            }
            tracker.Initialize(mapObject, state);
            return tracker;
        }

        public void AddSource(RangeCollider source)
        {
            if (source == null || _mapObject == null || _state == null)
            {
                return;
            }

            if (!_state.PlayerVisibleMapObjects.Contains(_mapObject))
            {
                _sources.Clear();
            }

            _sources.Add(source);
            _state.PlayerVisibleMapObjects.Add(_mapObject);
        }

        public void RemoveSource(RangeCollider source)
        {
            if (source == null)
            {
                return;
            }

            _sources.Remove(source);
            if (_sources.Count == 0)
            {
                RemoveFromVisibleSet();
            }
        }

        private void Initialize(MapObject mapObject, GameState state)
        {
            if (_mapObject != null && (_mapObject != mapObject || _state != state))
            {
                _sources.Clear();
            }
            _mapObject = mapObject;
            _state = state;
        }

        private void OnDisable()
        {
            RemoveFromVisibleSet();
            _sources.Clear();
        }

        private void OnDestroy()
        {
            RemoveFromVisibleSet();
            _sources.Clear();
            _visibleSurvivors.Clear();
            _mapObject = null;
            _state = null;
        }

        private void RemoveFromVisibleSet()
        {
            if (_state == null || ReferenceEquals(_mapObject, null))
            {
                return;
            }

            // Unity objects can enter their special destroyed state before managed teardown
            // completes. Rebuild the same public set by managed reference identity instead of
            // relying on Unity equality/hash behavior during disable/destruction. Reuse the
            // survivor buffer so the robust path does not reintroduce per-removal allocations.
            HashSet<MapObject> visibleObjects = _state.PlayerVisibleMapObjects;
            _visibleSurvivors.Clear();
            foreach (MapObject candidate in visibleObjects)
            {
                if (!ReferenceEquals(candidate, _mapObject))
                {
                    _visibleSurvivors.Add(candidate);
                }
            }

            visibleObjects.Clear();
            for (int i = 0; i < _visibleSurvivors.Count; i++)
            {
                visibleObjects.Add(_visibleSurvivors[i]);
            }
            _visibleSurvivors.Clear();
        }
    }
}
