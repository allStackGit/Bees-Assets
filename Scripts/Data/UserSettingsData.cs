using Assets.Scripts.Level;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class UserSettingsData : UserData
    {


        public List<HotKey> HotKeys = new List<HotKey>();
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
        };


        public UserSettingsData(bool shouldFileExist) : base()
        {
            defaultJsonData = DefaultJson();

            dynamic json = SetupFile(shouldFileExist, ConfigData.UserSettingsFilename, (json) =>
            {
                ConfigData.IsUserSettingsDataLoaded = true;
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                Dictionary<string, int[]> hotKeys = Utilities.JArrayToDictionary<string, int[]>(json.HotKeys);
                hotKeys.Keys.ToList().ForEach((hotKeyName) => HotKeys.Add(new HotKey(hotKeyName, hotKeys[hotKeyName].Select((k) => (KeyCode)k).ToList())));
            });

        }
        public void SetKey(string keyName, List<KeyCode> keys)
        {
            FindKey(keyName).Keys = keys;
            Save();
        }

        public HotKey FindKey(string name)
        {
            return HotKeys.FirstOrDefault(k => k.Name == name);
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
            Debug.Log(json);
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