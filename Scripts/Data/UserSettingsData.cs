using Assets.Scripts.Levels;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class UserSettingsData : UserData
    {
        public List<HotKey> HotKeys = new List<HotKey>();
        public Dictionary<string, HotKey> HotKeysByName = new Dictionary<string, HotKey>();
        public List<HotKey> DefaultHotKeys = new List<HotKey>
        {
            new HotKey("Match Speed", new List<KeyCode>{KeyCode.LeftShift, KeyCode.Q}),
            new HotKey("Attack on Sight", new List<KeyCode>{KeyCode.LeftShift, KeyCode.W}),
            new HotKey("Cease Fire", new List<KeyCode>{KeyCode.LeftShift, KeyCode.E}),
            new HotKey("Patrol", new List<KeyCode>{KeyCode.LeftShift, KeyCode.R}),
            new HotKey("Guard", new List<KeyCode>{KeyCode.LeftShift, KeyCode.T}),
            new HotKey("Chase", new List<KeyCode>{KeyCode.LeftShift, KeyCode.Y}),
            new HotKey("Hold", new List<KeyCode>{KeyCode.LeftShift, KeyCode.U}),
            new HotKey("Detonate", new List<KeyCode>{KeyCode.LeftShift, KeyCode.I}),
            new HotKey("Charge", new List<KeyCode>{KeyCode.LeftShift, KeyCode.O}),
            new HotKey("Drop Beacon", new List<KeyCode>{KeyCode.LeftShift, KeyCode.P}),

            new HotKey("First Seen", new List<KeyCode>{KeyCode.LeftShift, KeyCode.A}),
            new HotKey("Random", new List<KeyCode>{KeyCode.LeftShift, KeyCode.S}),
            new HotKey("Revenge", new List<KeyCode>{KeyCode.LeftShift, KeyCode.D}),
            new HotKey("Most Dangerous", new List<KeyCode>{KeyCode.LeftShift, KeyCode.F}),
            new HotKey("Most Health", new List<KeyCode>{KeyCode.LeftShift, KeyCode.G}),
            new HotKey("Least Health", new List<KeyCode>{KeyCode.LeftShift, KeyCode.H}),
            new HotKey("Most Powerful", new List<KeyCode>{KeyCode.LeftShift, KeyCode.J}),
            new HotKey("Least Powerful", new List<KeyCode>{KeyCode.LeftShift, KeyCode.K}),

            new HotKey("Closest", new List<KeyCode>{KeyCode.LeftShift, KeyCode.Z}),
            new HotKey("Furthest", new List<KeyCode>{KeyCode.LeftShift, KeyCode.X}),
            new HotKey("Most Range", new List<KeyCode>{KeyCode.LeftShift, KeyCode.C}),
            new HotKey("Least Range", new List<KeyCode>{KeyCode.LeftShift, KeyCode.V}),
            new HotKey("Fastest", new List<KeyCode>{KeyCode.LeftShift, KeyCode.B}),
            new HotKey("Slowest", new List<KeyCode>{KeyCode.LeftShift, KeyCode.N}),
            new HotKey("Most Valuable", new List<KeyCode>{KeyCode.LeftShift, KeyCode.M}),
            new HotKey("Least Valuable", new List<KeyCode>{KeyCode.LeftShift, KeyCode.Comma}),

            new HotKey("Select Squad #1", new List<KeyCode>{KeyCode.Alpha1}),
            new HotKey("Select Squad #2", new List<KeyCode>{KeyCode.Alpha2}),
            new HotKey("Select Squad #3", new List<KeyCode>{KeyCode.Alpha3}),
            new HotKey("Select Squad #4", new List<KeyCode>{KeyCode.Alpha4}),
            new HotKey("Select Squad #5", new List<KeyCode>{KeyCode.Alpha5}),
            new HotKey("Select Squad #6", new List<KeyCode>{KeyCode.Alpha6}),
            new HotKey("Select Squad #7", new List<KeyCode>{KeyCode.Alpha7}),
            new HotKey("Select Squad #8", new List<KeyCode>{KeyCode.Alpha8}),
            new HotKey("Select Squad #9", new List<KeyCode>{KeyCode.Alpha9}),
            new HotKey("Select Squad #0", new List<KeyCode>{KeyCode.Alpha0}),

            new HotKey("Open Menu", new List<KeyCode>{KeyCode.Escape}),
            new HotKey("Show Ranges", new List<KeyCode>{KeyCode.R}),
            new HotKey("Manual Fire", new List<KeyCode>{KeyCode.F}),
            new HotKey("Show Ranges + Manual Fire", new List<KeyCode>{KeyCode.F, KeyCode.R}),
            new HotKey("Toggle Mini Map", new List<KeyCode>{KeyCode.M}),

            new HotKey("Move Camera Up", new List<KeyCode>{KeyCode.W}),
            new HotKey("Move Camera Left", new List<KeyCode>{KeyCode.A}),
            new HotKey("Move Camera Down", new List<KeyCode>{KeyCode.S}),
            new HotKey("Move Camera Right", new List<KeyCode>{KeyCode.D}),
        };

        public HashSet<string> ContinuousInputActions = new HashSet<string>
        {
            "Move Camera Up",
            "Move Camera Left",
            "Move Camera Down",
            "Move Camera Right",
        };

        public HashSet<string> HeldDownInputActions = new HashSet<string>
        {
            "Show Ranges",
            "Manual Fire",
            "Show Ranges + Manual Fire"
        };

        public UserSettingsData(bool shouldFileExist) : base()
        {
            defaultJsonData = DefaultJson();

            SetupFile(shouldFileExist, ConfigData.UserSettingsFilename, loadedData =>
            {
                HotKeys.Clear();
                HotKeysByName.Clear();

                JObject json = AotJson.RequireObject(loadedData, ConfigData.UserSettingsFilename);
                Dictionary<string, int[]> hotKeys = AotJson.ParseStringIntArrayDictionary(json["HotKeys"]);
                foreach (string hotKeyName in hotKeys.Keys)
                {
                    AddHotKey(new HotKey(
                        hotKeyName,
                        hotKeys[hotKeyName].Select(k => (KeyCode)k).ToList(),
                        ContinuousInputActions.Contains(hotKeyName),
                        HeldDownInputActions.Contains(hotKeyName)));
                }

                // Saved settings are user overrides, not the complete schema. When a newer
                // build adds an action, older save files must inherit its default binding.
                foreach (HotKey defaultHotKey in DefaultHotKeys)
                {
                    if (!HotKeysByName.ContainsKey(defaultHotKey.Name))
                    {
                        AddHotKey(new HotKey(
                            defaultHotKey.Name,
                            defaultHotKey.Keys.ToList(),
                            ContinuousInputActions.Contains(defaultHotKey.Name),
                            HeldDownInputActions.Contains(defaultHotKey.Name)));
                    }
                }
                ConfigData.IsUserSettingsDataLoaded = true;
            });
        }

        private void AddHotKey(HotKey hotKey)
        {
            HotKeys.Add(hotKey);
            HotKeysByName.Add(hotKey.Name, hotKey);
        }

        public void SetKey(string keyName, List<KeyCode> keys)
        {
            FindKey(keyName).SetKeyCombination(keys);
        }

        public HotKey FindKey(string name)
        {
            return HotKeysByName[name];
        }

        public HotKey FindKeyByKeyString(string keyString)
        {
            return HotKeys.FirstOrDefault(k => k.KeyString == keyString);
        }

        public string DefaultJson()
        {
            string json = "{\"HotKeys\": [";

            for (int i = 0; i < DefaultHotKeys.Count; i++)
            {
                if (i < DefaultHotKeys.Count - 1)
                {
                    json += DefaultHotKeys[i].ToJson() + ",";
                }
                else
                {
                    json += DefaultHotKeys[i].ToJson();
                }
            }

            json += "]}";
            return json;
        }

        public override string ToJson()
        {
            string json = "{\"HotKeys\": [";

            for (int i = 0; i < HotKeys.Count; i++)
            {
                if (i < HotKeys.Count - 1)
                {
                    json += HotKeys[i].ToJson() + ",";
                }
                else
                {
                    json += HotKeys[i].ToJson();
                }
            }
            json += "]}";
            return json;
        }
    }
}
