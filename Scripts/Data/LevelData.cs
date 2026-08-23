using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
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

            SetupFile(shouldFileExist, ConfigData.LevelsDataFilenames[type], loadedData =>
            {
                // Recovery may invoke the loader again after a partially malformed payload. Clear
                // any levels appended by the failed pass before applying the fallback/default data.
                _levels.Clear();
                JObject json = AotJson.RequireObject(loadedData, ConfigData.LevelsDataFilenames[type]);
                JArray levels = json["Levels"] as JArray ?? new JArray();
                foreach (JObject level in levels.Children<JObject>())
                {
                    int id = level.Value<int>("Id");
                    int mapIndex = level.Value<int>("MapIndex");
                    if (type == (int)ConfigData.GameModes.Campaign)
                    {
                        mapIndex = CampaignMissionCatalog.Get(id).MapIndex;
                    }

                    List<SavedSquad> enemyReinforcements = AotJson.ParseSavedSquads(level["EnemyReinforcements"]);
                    List<SavedSquad> enemySquads = AotJson.ParseSavedSquads(level["EnemySquads"]);
                    List<(Vector2, Vector2)> obstacles = AotJson.ParseObstacles(level["ObstacleList"]);
                    List<int> enemyExistingSquads = level["EnemyExistingSquads"]?.ToObject<List<int>>() ?? new List<int>();
                    JObject userStartingPosition = (JObject)level["UserStartingPosition"];
                    JObject aiStartingPosition = (JObject)level["AIStartingPosition"];

                    _levels.Add(new LevelOptions(
                        id,
                        level.Value<int>("Side"),
                        level.Value<string>("Name"),
                        mapIndex,
                        level.Value<string>("Obstacles"),
                        obstacles,
                        level.Value<int>("AsteroidOption"),
                        level.Value<int>("FogOfWar"),
                        level.Value<int>("Mining"),
                        level.Value<bool>("HasPreLevelIntro"),
                        level.Value<bool>("HasSquadActionBox"),
                        level.Value<int>("SupplyCapacity"),
                        level.Value<int>("EnemyReinforcementsOption"),
                        level.Value<int>("EnemyReinforcementDelay"),
                        0,
                        0,
                        enemyReinforcements,
                        enemySquads,
                        enemyExistingSquads,
                        level.Value<string>("EnemyReport"),
                        new List<SavedSquad>(),
                        new Vector2(
                            userStartingPosition.Value<float>("x"),
                            userStartingPosition.Value<float>("y")),
                        new Vector2(
                            aiStartingPosition.Value<float>("x"),
                            aiStartingPosition.Value<float>("y"))));
                }
                ConfigData.IsLevelsDataLoaded[type] = true;
            });
        }

        public List<LevelOptions> GetLevels()
        {
            // Persisted/server JSON order is not a campaign identity. Several legacy callers use
            // list position as the mission index, so expose a deterministic ID order here rather
            // than allowing database row order to select the wrong mission/map/squads.
            return _levels.OrderBy(level => level.Id).ToList();
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
