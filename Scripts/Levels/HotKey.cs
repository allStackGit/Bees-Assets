using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class HotKey
    {
        public List<KeyCode> Keys;
        public string Name, KeyString;
        public Action Action;
        /// <summary>
        /// The action that occurs when the any of the keys is released. Must be set in UserSettingsData.HeldDownInputActions as well
        /// </summary>
        public Action ReleaseAction;
        private bool _hasInputRelease = true;
        /// <summary>
        /// This action keeps happening as long as the key is held down
        /// </summary>
        private bool _hasContinuousInput;
        /// <summary>
        /// This action stops as soon as the key is released and a new action is performed
        /// </summary>
        private bool _hasReleaseAction;
        private float _lastInputTime;
        public int Id;

        public HotKey(string name, List<KeyCode> keys, Action action, Action releaseAction = null, bool hasContinuousInput = false, bool hasReleaseAction = false)
        {
            Name = name;
            Keys = keys;
            Id = Name.GetHashCode();
            _hasContinuousInput = hasContinuousInput;
            _hasReleaseAction = hasReleaseAction;
            SetReleaseAction(releaseAction);
            SetAction(action);
            MakeKeyString();
        }
        public HotKey(string name, List<KeyCode> keys, bool hasContinuousInput = false, bool hasReleaseAction = false)
        {
            Name = name;
            Keys = keys;
            Id = Name.GetHashCode();
            _hasContinuousInput = hasContinuousInput;
            _hasReleaseAction = hasReleaseAction;
            MakeKeyString();
        }
        public void MakeKeyString()
        {
            KeyString = Keys.Aggregate("", (a, b) => a.ToString().Length > 0 ? $"{a} + {b}" : $"{b}");
        }
        public void SetAction(Action action) { 
            Action = action; 
        }
        public void SetReleaseAction(Action action)
        {
            ReleaseAction = action;
        }
        public void SetKeyCombination(List<KeyCode> keys)
        {
            Keys = keys;
            MakeKeyString();
        }
        public bool CheckInput()
        {
            if (HasInput())
            {
                Action();
                return true;
            }
            else
            {
                if (CheckInputRelease() && _hasReleaseAction)
                {
                    ReleaseAction();
                }
                return false;
            }
        }

        public bool HasInput()
        {
            List<KeyCode> keysPressed = Utilities.GetAllKeys();
            if (_hasInputRelease && Keys.All((k) => keysPressed.Contains(k)) && keysPressed.All((k) => Keys.Contains(k)))
            {
                //Debug.Log($"Keys pressed for input: {Utilities.ListToString(keysPressed)}");
                _hasInputRelease = false;
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool CheckInputRelease()
        {
            if (_hasContinuousInput && Time.realtimeSinceStartup - _lastInputTime > .05f)
            {
                _lastInputTime = Time.realtimeSinceStartup;
                _hasInputRelease = true;
            }
            if (!_hasInputRelease && Keys.Any(k => Input.GetKeyUp(k)))
            {
                _hasInputRelease = true;
                return true;
            }
            return false;
        }
        public void ManuallySetInputRelease(bool value)
        {
            _hasInputRelease = value;
        }
        public string ToJson()
        {
            return $"{{\"{Name}\": [{Keys.Aggregate("", (a, b) => a.ToString().Length > 0 ? $"{a}, {(int) b}" : $"{(int) b}")}]}}";
        }
        public override string ToString()
        {
            return $"{Name}#{Id}: {KeyString}";
        }
        public bool Equals(HotKey other)
        {
            return Id == other.Id;
        }
        private HotKey _hotKey;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _hotKey = obj as HotKey;
            if (_hotKey == null)
            {
                return false;
            }

            return Id == _hotKey.Id;
        }
        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(HotKey a, HotKey b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            // Return true if the fields match:
            return a.Id == b.Id;
        }

        public static bool operator !=(HotKey a, HotKey b)
        {
            return !(a == b);
        }

    }
}