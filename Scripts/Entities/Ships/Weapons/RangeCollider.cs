using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
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
                if (!_shipEnter.IsDead)
                {
                    Weapon.ShipsWithinRange.Add(_shipEnter.Id, _shipEnter);
                    //try
                    //{
                    //    Weapon.ShipsWithinRange.Add(_shipEnter.Id, _shipEnter);

                    //}
                    //catch (Exception e)
                    //{
                    //    Debug.Log(Weapon);
                    //    Debug.Log(Weapon.ShipsWithinRange);
                    //    Debug.Log(_shipEnter);
                    //    throw e;
                    //}
                    _shipEnter.WeaponsThatHaveUsWithinRange.Add(Weapon);
                    Weapon.HasCachedChanged = true;
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
                        mapObject.AddPlayerVisibilitySource(this, Weapon.Ship.Level.State);
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

                //Debug.Log($"{ship.Name} is no longer in {Weapon.Ship.Name} range");
                Weapon.ShipsWithinRange.Remove(_shipExit.Id);
                Weapon.HasCachedChanged = true;
                if (!_shipExit.IsDead)
                {
                    _shipExit.WeaponsThatHaveUsWithinRange.Remove(Weapon);
                }


                //if (Weapon.Ship.IsHiveMindControlled)
                //{
                //    Level.State.HivemindShips[Weapon.Side - 1][Weapon.Ship.Id].Remove(ship);
                //}
            }
            else if (_colliderExit.CompareTag("Projectile"))
            {
                _projectileExit = _colliderExit.GetComponent<Projectile>();
                if (_projectileExit.Weapon.Equals(Weapon)
                    && !Weapon.Ship.IsDead
                    && _projectileExit.Type != ConfigData.ProjectileTypes.RocketExplosion
                    && _projectileExit.Type != ConfigData.ProjectileTypes.FireTankExplosion)
                {
                    //Debug.Log($"{Weapon.Ship.Name}'s projectile left it's range!");
                    //if (_projectileExit.Type == ConfigData.ProjectileTypes.Rocket)
                    //{
                    //    _projectileExit.KillSequence();
                    //}
                    //else
                    //{
                    //    _projectileExit.Kill();
                    //}
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
                        mapObject.RemovePlayerVisibilitySource(this, Weapon.Ship.Level.State);
                    }
                }
            }
        }

        private void ClearVisibleMapObjects()
        {
            GameState state = Weapon != null && Weapon.Ship != null && Weapon.Ship.Level != null
                ? Weapon.Ship.Level.State
                : null;
            if (state != null)
            {
                foreach (MapObject mapObject in _visibleMapObjectContacts.Keys)
                {
                    if (mapObject != null)
                    {
                        mapObject.RemovePlayerVisibilitySource(this, state);
                    }
                }
            }
            _visibleMapObjectContacts.Clear();
        }
    }
}
