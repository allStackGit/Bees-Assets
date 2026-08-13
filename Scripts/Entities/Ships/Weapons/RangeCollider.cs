using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class RangeCollider : MonoBehaviour
    {
        public Weapon Weapon;
        public int Range;
        public CircleCollider2D Collider;

        private readonly Dictionary<MapObject, int> _visibleMapObjectContacts = new Dictionary<MapObject, int>();

        public virtual void Create(Weapon weapon, int range)
        {
            Weapon = weapon;
            Range = range;
            Weapon.HasRangeCollider = true;
            Collider.radius = Range;
        }
        public void Activate()
        {
            Collider.enabled = true;
            enabled = true;
        }
        public void Deactivate()
        {
            ClearVisibleMapObjects();
            Collider.enabled = false;
            enabled = false;
        }
        private Ship _shipEnter;
        private GameObject _colliderEnter;
        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            _colliderEnter = collider.gameObject;
            if (_colliderEnter.CompareTag("Ship"))
            {
                _shipEnter = _colliderEnter.GetComponent<Ship>();
                if (_shipEnter != null &&
                    !_shipEnter.IsDead &&
                    Weapon != null &&
                    Weapon.Ship != null &&
                    _shipEnter.Side != Weapon.Ship.Side &&
                    !Weapon.ShipsWithinRange.ContainsKey(_shipEnter.Id))
                {
                    // A weapon's range cache is an enemy-candidate cache, not a raw physics
                    // contact cache. Keeping friendly ships out here prevents every downstream
                    // targeting pass from scanning/re-sorting friendlies and prevents fallback
                    // targeting from ever treating a formation mate as an enemy.
                    Weapon.ShipsWithinRange.Add(_shipEnter.Id, _shipEnter);
                    _shipEnter.WeaponsThatHaveUsWithinRange.Add(Weapon);
                    Weapon.HasCachedChanged = true;
                    Weapon.Stage.DebugLogger?.RecordWeaponRangeEnter();
                }

            }
            else if (_colliderEnter.CompareTag("Object"))
            {
                MapObject mapObject = _colliderEnter.GetComponent<MapObject>();
                if (mapObject != null)
                {
                    if (_visibleMapObjectContacts.TryGetValue(mapObject, out int contacts))
                    {
                        _visibleMapObjectContacts[mapObject] = contacts + 1;
                    }
                    else
                    {
                        _visibleMapObjectContacts.Add(mapObject, 1);
                        GetVisibilityTracker(mapObject)?.AddSource(this);
                    }
                }
            }

        }
        private Ship _shipExit;
        private Projectile _projectileExit;
        private GameObject _colliderExit; 
        protected virtual void OnTriggerExit2D(Collider2D collider) // This is triggered by ships dying too 
        {
            _colliderExit = collider.gameObject;
            if (_colliderExit.CompareTag("Ship"))
            {
                _shipExit = _colliderExit.GetComponent<Ship>();

                if (_shipExit != null && Weapon.ShipsWithinRange.Remove(_shipExit.Id))
                {
                    Weapon.HasCachedChanged = true;
                    if (!_shipExit.IsDead)
                    {
                        _shipExit.WeaponsThatHaveUsWithinRange.Remove(Weapon);
                    }
                }
            }
            else if (_colliderExit.CompareTag("Projectile"))
            {
                _projectileExit = _colliderExit.GetComponent<Projectile>();
                if (_projectileExit.Weapon.Equals(Weapon)
                    && !Weapon.Ship.IsDead
                    && _projectileExit.Type != ConfigData.ProjectileTypes.RocketExplosion
                    && _projectileExit.Type != ConfigData.ProjectileTypes.FireTankExplosion)
                {
                    _projectileExit.Kill();
                }
            }
            else if (_colliderExit.CompareTag("Object"))
            {
                MapObject mapObject = _colliderExit.GetComponent<MapObject>();
                if (mapObject != null && _visibleMapObjectContacts.TryGetValue(mapObject, out int contacts))
                {
                    if (contacts > 1)
                    {
                        _visibleMapObjectContacts[mapObject] = contacts - 1;
                    }
                    else
                    {
                        _visibleMapObjectContacts.Remove(mapObject);
                        MapObjectVisibilityTracker tracker = mapObject.GetComponent<MapObjectVisibilityTracker>();
                        tracker?.RemoveSource(this);
                    }
                }
            }
        }

        private MapObjectVisibilityTracker GetVisibilityTracker(MapObject mapObject)
        {
            GameState state = Weapon != null && Weapon.Ship != null && Weapon.Ship.Level != null
                ? Weapon.Ship.Level.State
                : null;
            return MapObjectVisibilityTracker.GetOrCreate(mapObject, state);
        }

        private void ClearVisibleMapObjects()
        {
            foreach (MapObject mapObject in _visibleMapObjectContacts.Keys)
            {
                if (mapObject != null)
                {
                    MapObjectVisibilityTracker tracker = mapObject.GetComponent<MapObjectVisibilityTracker>();
                    tracker?.RemoveSource(this);
                }
            }
            _visibleMapObjectContacts.Clear();
        }
    }
}
