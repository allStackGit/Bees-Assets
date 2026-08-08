using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// Runtime companion added only while a MapObject is visible to player range sources.
    /// It owns the set of observing ranges so one range exiting cannot hide an object
    /// still observed by another, and destruction removes the object from GameState.
    /// </summary>
    public sealed class MapObjectVisibilityTracker : MonoBehaviour
    {
        private readonly HashSet<RangeCollider> _sources = new HashSet<RangeCollider>();
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

            // ResetState clears the authoritative public set. If this object survived
            // that reset, stale pre-reset source ownership must not leak forward.
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
            if (_sources.Count == 0 && _state != null && _mapObject != null)
            {
                _state.PlayerVisibleMapObjects.Remove(_mapObject);
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

        private void OnDestroy()
        {
            // Unity can make a component compare equal to null during object teardown.
            // ReferenceEquals preserves the managed reference long enough to remove the
            // MapObject from its ID-based HashSet deterministically.
            if (_state != null && !ReferenceEquals(_mapObject, null))
            {
                _state.PlayerVisibleMapObjects.Remove(_mapObject);
            }
            _sources.Clear();
            _mapObject = null;
            _state = null;
        }
    }
}
