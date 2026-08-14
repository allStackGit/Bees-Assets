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

            HashSet<MapObject> visibleObjects = _state.PlayerVisibleMapObjects;

            // Normal runtime objects have a stable nonzero gameplay Id, so HashSet.Remove
            // is O(1). A live pre-Setup object also has a stable Unity instance hash.
            bool isLiveUnityObject = (UnityEngine.Object)_mapObject != null;
            if ((_mapObject.Id != 0 || isLiveUnityObject) && visibleObjects.Remove(_mapObject))
            {
                return;
            }

            if (visibleObjects.Count == 0)
            {
                return;
            }

            // Defensive fallback for destroyed/uninitialized Unity wrappers whose hash can no
            // longer be trusted. Preserve the old reference-based removal semantics only here.
            _visibleSurvivors.Clear();
            bool foundReference = false;
            foreach (MapObject candidate in visibleObjects)
            {
                if (ReferenceEquals(candidate, _mapObject))
                {
                    foundReference = true;
                }
                else
                {
                    _visibleSurvivors.Add(candidate);
                }
            }

            if (foundReference)
            {
                visibleObjects.Clear();
                for (int i = 0; i < _visibleSurvivors.Count; i++)
                {
                    visibleObjects.Add(_visibleSurvivors[i]);
                }
            }
            _visibleSurvivors.Clear();
        }
    }
}
