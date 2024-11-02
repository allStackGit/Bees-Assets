using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level
{
    public class HotKey
    {
        public List<KeyCode> Keys;
        public string Name, KeyString;
        public Action Action;
        private bool _hasInputRelease = true;

        public HotKey(string name, List<KeyCode> keys, Action action)
        {
            Name = name;
            Keys = keys;
            SetAction(action);
            KeyString = Keys.Aggregate("", (a, b) => a.ToString().Length > 0 ? $"{a} + {b}" : $"{b}");
        }
        public HotKey(string name, List<KeyCode> keys)
        {
            Name = name;
            Keys = keys;
            KeyString = Keys.Aggregate("", (a, b) => a.ToString().Length > 0 ? $"{a} + {b}" : $"{b}");
        }
        public void SetAction(Action action) { 
            Action = action; 
        }
        public bool Checkinput()
        {
            if (HasInput())
            {
                Action();
                return true;
            }
            else
            {
                CheckInputRelease();
                return false;
            }
        }

        public bool HasInput()
        {
            if (_hasInputRelease && Keys.All(k => Input.GetKey(k)))
            {
                _hasInputRelease = false;
                return true;
            }
            else
            {
                return false;
            }
        }

        public void CheckInputRelease()
        {
            if (!_hasInputRelease && Keys.Any(k => Input.GetKeyUp(k)))
            {
                _hasInputRelease = true;
            }
        }

        public string ToJson()
        {
            return $"{{\"{Name}\": [{Keys.Aggregate("", (a, b) => a.ToString().Length > 0 ? $"{a}, {(int) b}" : $"{(int) b}")}]}}";
        }

    }
}