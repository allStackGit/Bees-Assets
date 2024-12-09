using Assets.Scripts.Scenes;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for all the levels
    public class LevelData : UserData
    {
        public List<Level> Levels = new List<Level>();

        public LevelData(bool shouldFileExist) : base()
        {
            defaultJsonData = "{\"Levels\": []}";

            dynamic json = SetupFile(shouldFileExist, ConfigData.LevelsDataFilename, (json) =>
            {
                ConfigData.IsLevelsDataLoaded = true;
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                List<dynamic> levels = Utilities.JArrayToList<dynamic>(json.Levels);
                levels.ForEach(level =>
                {
                    List<SavedSquad> enemyReinforcements = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemyReinforcements));
                    List<SavedSquad> enemySquads = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemySquads));

                    Levels.Add(new Level((int)level.Id, (int)level.Side, level.name, (int)level.MapIndex, (int)level.ObstacleMapIndex, (int)level.AsteroidOption, (int)level.FogOfWar,
                        (int)level.Mining, (int)level.SupplyCapacity, (int)level.EnemyReinforcementDelay, enemyReinforcements, enemySquads));
                });
            });

        }

        public override string ToJson()
        {
            string json = "{\"Levels\": [";
            for (int i = 0; i < Levels.Count; i++)
            {
                if (i < Levels.Count - 1)
                {
                    json += Levels[i].ToJson() + ",";
                }
                else
                {
                    json += Levels[i].ToJson();
                }
            }
            json += "]}";
            return json;
        }
        public void AddLevel(Level level)
        {
            Levels.Add(level);
        }
        public int GetNewId()
        {
            return Levels.Count;
        }

        
    }
}