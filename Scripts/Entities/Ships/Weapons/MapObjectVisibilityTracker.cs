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

            // ResetState or deactivation cleanup clears the authoritative public set.
            // If this object survives and is observed again, stale ownership must not leak forward.
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
            // Unity invokes OnDisable as an active object/component leaves service, including
            // normal destruction and pooling/deactivation. Visibility must be gone at that point;
            // waiting only for OnDestroy can leave stale Unity-object references in EditMode and
            // also misses objects that are disabled for reuse rather than destroyed.
            RemoveFromVisibleSet();
            _sources.Clear();
        }

        private void OnDestroy()
        {
            // Idempotent fallback for teardown paths where OnDisable has already run.
            RemoveFromVisibleSet();
            _sources.Clear();
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
            // completes. Rebuild the same public HashSet instance by managed reference identity
            // rather than relying on the object's equality/hash behavior during destruction.
            HashSet<MapObject> visibleObjects = _state.PlayerVisibleMapObjects;
            List<MapObject> survivors = new List<MapObject>(visibleObjects.Count);
            foreach (MapObject candidate in visibleObjects)
            {
                if (!ReferenceEquals(candidate, _mapObject))
                {
                    survivors.Add(candidate);
                }
            }

            visibleObjects.Clear();
            foreach (MapObject survivor in survivors)
            {
                visibleObjects.Add(survivor);
            }
        }
    }
}
