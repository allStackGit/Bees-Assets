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
        private readonly HashSet<RangeCollider>[] _sourcesBySide =
        {
            new HashSet<RangeCollider>(),
            new HashSet<RangeCollider>()
        };
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

            int side = GetSourceSide(source);
            HashSet<MapObject> visibleObjects = _state.GetPlayerVisibleMapObjects(side);
            if (visibleObjects == null)
            {
                return;
            }

            HashSet<RangeCollider> sources = _sourcesBySide[side - 1];
            if (!visibleObjects.Contains(_mapObject))
            {
                sources.Clear();
            }

            sources.Add(source);
            visibleObjects.Add(_mapObject);
        }

        public void RemoveSource(RangeCollider source)
        {
            if (source == null)
            {
                return;
            }

            for (int sideIndex = 0; sideIndex < _sourcesBySide.Length; sideIndex++)
            {
                HashSet<RangeCollider> sources = _sourcesBySide[sideIndex];
                if (sources.Remove(source) && sources.Count == 0)
                {
                    RemoveFromVisibleSet(sideIndex + 1);
                }
            }
        }

        private static int GetSourceSide(RangeCollider source)
        {
            return source != null && source.Weapon != null && source.Weapon.Ship != null
                ? source.Weapon.Ship.Side
                : 0;
        }

        private void Initialize(MapObject mapObject, GameState state)
        {
            if (_mapObject != null && (_mapObject != mapObject || _state != state))
            {
                for (int sideIndex = 0; sideIndex < _sourcesBySide.Length; sideIndex++)
                {
                    _sourcesBySide[sideIndex].Clear();
                }
            }
            _mapObject = mapObject;
            _state = state;
        }

        /// <summary>
        /// Called directly by MapObject's owner lifecycle as well as by this component's Unity
        /// callbacks. Keeping the owner as an explicit caller makes teardown deterministic in
        /// EditMode, pooled-object deactivation, and ordinary runtime destruction alike.
        /// </summary>
        internal void HandleOwnerUnavailable()
        {
            for (int sideIndex = 0; sideIndex < _sourcesBySide.Length; sideIndex++)
            {
                RemoveFromVisibleSet(sideIndex + 1);
                _sourcesBySide[sideIndex].Clear();
            }
        }

        private void OnDisable()
        {
            HandleOwnerUnavailable();
        }

        private void OnDestroy()
        {
            HandleOwnerUnavailable();
            _visibleSurvivors.Clear();
            _mapObject = null;
            _state = null;
        }

        private void RemoveFromVisibleSet(int side)
        {
            if (_state == null || ReferenceEquals(_mapObject, null))
            {
                return;
            }

            // Unity objects can enter their special destroyed state before managed teardown
            // completes. Rebuild the side-owned set by managed reference identity instead of
            // relying on Unity equality/hash behavior during disable/destruction. Reuse the
            // survivor buffer so the robust path does not reintroduce per-removal allocations.
            HashSet<MapObject> visibleObjects = _state.GetPlayerVisibleMapObjects(side);
            if (visibleObjects == null)
            {
                return;
            }
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
