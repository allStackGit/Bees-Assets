using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for a list of levels
    public class LevelData : UserData
    {
        private List<LevelOptions> _levels = new List<LevelOptions>();

        public LevelData(bool shouldFileExist, int type) : base()
        {
            defaultJsonData = "{\"Levels\": []}";

            dynamic json = SetupFile(shouldFileExist, ConfigData.LevelsDataFilenames[type], (json) =>
            {
                ConfigData.IsLevelsDataLoaded[type] = true;
                List<dynamic> levels = Utilities.JArrayToList<dynamic>(json.Levels);
                levels.ForEach(level =>
                {
                    int id = (int)level.Id;
                    int mapIndex = (int)level.MapIndex;
                    if (type == (int)ConfigData.GameModes.Campaign)
                    {
                        mapIndex = CampaignMissionCatalog.Get(id).MapIndex;
                    }

                    List<SavedSquad> enemyReinforcements = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemyReinforcements));
                    List<SavedSquad> enemySquads = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemySquads));
                    List<(Vector2, Vector2)> obstacles = Utilities.LoadObstaclesFromJson(Utilities.JArrayToList<dynamic>(level.ObstacleList));
                    List<int> enemyExistingSquads = Utilities.JArrayToList<int>(level.EnemyExistingSquads);
                    _levels.Add(new LevelOptions(id, (int)level.Side, (string)level.Name, mapIndex, (string)level.Obstacles, obstacles, (int)level.AsteroidOption, (int)level.FogOfWar, (int)level.Mining, (bool) level.HasPreLevelIntro, (bool) level.HasSquadActionBox, (int)level.SupplyCapacity, (int) level.EnemyReinforcementsOption, (int)level.EnemyReinforcementDelay, 0, 0, enemyReinforcements, enemySquads, enemyExistingSquads, (string) level.EnemyReport, new List<SavedSquad>(), new Vector2((float)level.UserStartingPosition.x, (float)level.UserStartingPosition.y), new Vector2((float)level.AIStartingPosition.x, (float)level.AIStartingPosition.y)));
                });
            });

        }
        public List<LevelOptions> GetLevels()
        {
            return _levels;
        }
        public LevelOptions GetLevel(int levelId)
        {
            Debug.Log($"Getting level #{levelId}");
            LevelOptions level = _levels.FirstOrDefault(candidate => candidate.Id == levelId);
            if (level == null)
            {
                Debug.LogError($"Could not find persisted level with Id #{levelId}. Loaded IDs: {string.Join(", ", _levels.Select(candidate => candidate.Id))}");
            }
            return level;
        }

        public override string ToJson()
        {
            string json = "{\"Levels\": [";
            for (int i = 0; i < _levels.Count; i++)
            {
                if (i < _levels.Count - 1)
                {
                    json += _levels[i].ToJson() + ",";
                }
                else
                {
                    json += _levels[i].ToJson();
                }
            }
            json += "]}";
            return json;
        }
        public void AddLevel(LevelOptions level)
        {
            _levels.Add(level);
        }
        public int GetCurrentId()
        {
            return _levels.Any() ? _levels.Max(level => level.Id) : -1;
        }
        public int GetNewId()
        {
            return GetCurrentId() + 1;
        }
    }
}
