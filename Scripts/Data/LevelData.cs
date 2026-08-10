using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for a list of levels
    public class LevelData : UserData
    {
        private List<LevelOptions> _levels = new List<LevelOptions>();
        private int _type;

        public LevelData(bool shouldFileExist, int type) : base()
        {
            _type = type;
            defaultJsonData = "{\"Levels\": []}";

            dynamic json = SetupFile(shouldFileExist, ConfigData.LevelsDataFilenames[type], (json) =>
            {
                ConfigData.IsLevelsDataLoaded[type] = true;
                List<dynamic> levels = Utilities.JArrayToList<dynamic>(json.Levels);
                levels.ForEach(level =>
                {
                    List<SavedSquad> enemyReinforcements = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemyReinforcements));
                    List<SavedSquad> enemySquads = Utilities.LoadSquadsFromJson(Utilities.JArrayToList<dynamic>(level.EnemySquads));
                    List<(Vector2, Vector2)> obstacles = Utilities.LoadObstaclesFromJson(Utilities.JArrayToList<dynamic>(level.ObstacleList));
                    List<int> enemyExistingSquads = Utilities.JArrayToList<int>(level.EnemyExistingSquads);
                    _levels.Add(new LevelOptions((int)level.Id, (int)level.Side, (string)level.Name, (int)level.MapIndex, (string)level.Obstacles, obstacles, (int)level.AsteroidOption, (int)level.FogOfWar, (int)level.Mining, (bool) level.HasPreLevelIntro, (bool) level.HasSquadActionBox, (int)level.SupplyCapacity, (int) level.EnemyReinforcementsOption, (int)level.EnemyReinforcementDelay, 0, 0, enemyReinforcements, enemySquads, enemyExistingSquads, (string) level.EnemyReport, new List<SavedSquad>(), new Vector2((float)level.UserStartingPosition.x, (float)level.UserStartingPosition.y), new Vector2((float)level.AIStartingPosition.x, (float)level.AIStartingPosition.y)));
                });

                if (_type == 1)
                {
                    LogCampaignCatalogMismatches();
                }
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

            if (_type == 1)
            {
                CampaignMissionCatalog.MissionDefinition mission;
                try
                {
                    mission = CampaignMissionCatalog.Get(levelId);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new InvalidOperationException(
                        $"Campaign progress references unknown mission Id #{levelId}.", exception);
                }

                if (!mission.HasPersistedLevelData)
                {
                    throw new InvalidOperationException(
                        $"Campaign mission #{levelId} ({mission.Name}) has runtime logic but no persisted campaign level data.");
                }
                if (level == null)
                {
                    throw new InvalidOperationException(
                        $"Campaign mission #{levelId} ({mission.Name}) is missing from persisted campaign level data. " +
                        $"Loaded IDs: {string.Join(", ", _levels.Select(candidate => candidate.Id))}");
                }
                if (!MissionNamesMatch(level.Name, mission.Name))
                {
                    throw new InvalidOperationException(
                        $"Campaign level data is out of date: Id #{levelId} is '{level.Name}' in persisted data " +
                        $"but the runtime catalog expects '{mission.Name}'.");
                }
                return level;
            }

            if (level == null)
            {
                Debug.LogError($"Could not find persisted level with Id #{levelId}. Loaded IDs: {string.Join(", ", _levels.Select(candidate => candidate.Id))}");
            }
            return level;
        }

        private void LogCampaignCatalogMismatches()
        {
            foreach (CampaignMissionCatalog.MissionDefinition mission in CampaignMissionCatalog.Definitions)
            {
                if (!mission.HasPersistedLevelData)
                {
                    continue;
                }

                List<LevelOptions> matches = _levels.Where(level => level.Id == mission.Id).ToList();
                if (matches.Count != 1)
                {
                    Debug.LogError(
                        $"Campaign level data mismatch for #{mission.Id} ({mission.Name}): expected exactly one record, found {matches.Count}.");
                    continue;
                }
                if (!MissionNamesMatch(matches[0].Name, mission.Name))
                {
                    Debug.LogError(
                        $"Campaign level data mismatch for #{mission.Id}: persisted name '{matches[0].Name}' does not match catalog name '{mission.Name}'.");
                }
            }
        }

        private static bool MissionNamesMatch(string persistedName, string catalogName)
        {
            return NormalizeMissionName(persistedName) == NormalizeMissionName(catalogName);
        }

        private static string NormalizeMissionName(string name)
        {
            return new string((name ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
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