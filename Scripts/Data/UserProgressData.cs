using Assets.Scripts.Scenes;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for user progress data
    public class UserProgressData : UserData
    {
        public int CurrentLevel = -1; // a level of -1 indicates that the level data hasn't been loaded yet
        public int SavedSquadId = -1; //[alert] [reminder]  this starts at 1 because there are two starting squads 0, and 1. The next Id should be 2.

        public UserProgressData(bool shouldFileExist): base()
        {
            defaultJsonData = "{\"CurrentLevel\": 1, \"SavedSquadId\": -1}";
            
            dynamic json = SetupFile(shouldFileExist, ConfigData.UserProgressFilename, (json) =>
            {
                ConfigData.IsUserProgressDataLoaded = true;
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                CurrentLevel = json.CurrentLevel;
                SavedSquadId = json.SavedSquadId;
            });
            
        }
        public void SetCurrentLevel(int level)
        {
            if (level != CurrentLevel)
            {
                CurrentLevel = level;
                Save();
            }
        }
        public int GetNextSavedSquadId()
        {
            SavedSquadId++;
            Save();
            return SavedSquadId;
            
        }

        public override string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
    }
}