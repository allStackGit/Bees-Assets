using Assets.Scripts.Scenes;
using NUnit.Framework;
using System.Collections.Generic;
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
                //Debug.Log($"Setting up LevelData file for {ConfigData.LevelsDataFilenames[type]}");
                //Debug.Log($"JSON from DataFile: {json}");
                List<dynamic> levels = Utilities.JArrayToList<dynamic>(json.Levels);
                levels.ForEach(level =>
                {
                    List<SavedSquad> enemyReinforcements = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemyReinforcements));
                    List<SavedSquad> enemySquads = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemySquads));

                    _levels.Add(new LevelOptions((int)level.Id, (int)level.Side, (string)level.Name, (int)level.MapIndex, (int)level.ObstacleMapIndex, (int)level.AsteroidOption, (int)level.FogOfWar,
                        (int)level.Mining, (int)level.SupplyCapacity, (int) level.EnemyReinforcementsOption, (int)level.EnemyReinforcementDelay, 0, 0, enemyReinforcements, enemySquads, new List<SavedSquad>()));
                });
            });

        }
        public List<LevelOptions> GetLevels()
        {
            return _levels;
        }
        public LevelOptions GetLevel(int levelId)
        {
            return _levels[levelId];
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
            return GetNewId() - 1;
        }
        public int GetNewId()
        {
            return _levels.Count;
        }

        
    }
}