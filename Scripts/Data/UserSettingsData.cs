using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class UserSettingsData : UserData
    {

        public Dictionary<string, List<KeyCode>> HotKeys = new Dictionary<string, List<KeyCode>>();
        public List<int> MatchSpeedKeys;


        public UserSettingsData(bool shouldFileExist) : base()
        {
            defaultJsonData = "{\"MatchSpeedKeys\": [304, 113]}";

            dynamic json = SetupFile(shouldFileExist, ConfigData.UserSettingsFilename, (json) =>
            {
                ConfigData.IsUserSettingsDataLoaded = true;
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                MatchSpeedKeys = Utilities.JArrayToList<int>(json.MatchSpeedKeys);
                HotKeys.Add("Match Speed", MatchSpeedKeys.Select((k) => (KeyCode) k).ToList());
            });

        }
        public void SetKey(string keyType, List<KeyCode> keys)
        {
            switch (keyType)
            {
                case "Match Speed":
                    MatchSpeedKeys = keys.Select((k) => (int) k).ToList();
                    break;
            }
            Save();
        }


        public override string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
    }
}