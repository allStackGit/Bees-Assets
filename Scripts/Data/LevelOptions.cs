using Assets.Scripts.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static UnityEngine.RuleTile.TilingRuleOutput;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Assets.Scripts.Data
{
    public class LevelOptions : ICloneable
    {
        /// <summary>
        /// The Unique Id of the level. Level data is stored on the server and any time a level is added it's added for all users
        /// </summary>
        public int Id;
        /// <summary>
        /// Since the player side chooses their ships, every level has to have a side where the side is which side has preset ships
        /// </summary>
        public int Side;
        /// <summary>
        /// The name given to the level
        /// </summary>
        public string Name;
        /// <summary>
        /// The index of the map that the level has
        /// -1 = Random
        /// 0 = Pluto
        /// 1 = Neptune
        /// 2 = Titania
        /// 3 = Uranus
        /// </summary>
        public int MapIndex;
        /// <summary>
        /// The file name of the set of obstacles that need to be loaded in. null means no obstacles and "" means random obstacles.
        /// </summary>
        public string Obstacles;
        /// <summary>
        /// Used if there are randomly generated obstacles. A list of vector pairs relating to the position and scale of the obstacle, respectively
        /// </summary>
        public List<(Vector2, Vector2)> ObstacleList;
        /// <summary>
        /// Whether or not the map has asteroids
        /// -1 = Random
        /// 0 = No asteroids
        /// 1 = Normal asteroids
        /// 2 = Twice as many asteroids
        /// </summary>
        public int AsteroidOption;
        /// <summary>
        /// Whether or not the map has Fog of War
        /// -1 = Random
        /// 0 = No
        /// 1 = Yes
        /// </summary>
        public int FogOfWar;
        /// <summary>
        /// Whether or not the map has Mining
        /// -1 = Random
        /// 0 = No
        /// 1 = Yes
        /// </summary>
        public int Mining;
        /// <summary>
        /// Whether or not the level has a prelevel intro cutscene or dialogue before the player selects ships or starts the level
        /// </summary>
        public bool HasPreLevelIntro;
        /// <summary>
        /// Whether or not to show the squad action box for the player
        /// </summary>
        public bool HasSquadActionBox;
        /// <summary>
        /// The maximum amount of TSV the player can use for this level
        /// </summary>
        public int SupplyCapacity;
        /// <summary>
        /// Whether or not the map has enemy reinforcements
        /// -1 = Random
        /// 0 = No
        /// 1 = Yes
        /// </summary>
        public int EnemyReinforcementsOption;
        /// <summary>
        /// The amount of time (in seconds) before enemy reinforcement spawn outside of the map. A time of 0 indicates no reinforcements
        /// </summary>
        public int EnemyReinforcementDelay;
        /// <summary>
        /// Whether the enemy squads are only of a particular ship type and if so, what ship type. Does not get permanantly stored because the actual ships in the level get stored instead
        /// -1 = Random, any ship type
        /// 0  = All ship types
        /// 1+ = The index of the ship type
        /// </summary>
        public int EnemyShipTypeOption;
        /// <summary>
        /// The upper limit on how many enemy squads should be randomly generated
        /// 0 = No squads randomly generated, enemy squads are already picked
        /// 1+ = The maximum number of squads to generate
        /// </summary>
        public int EnemySquadGenerationCount;
        /// <summary>
        /// The enemy squad compositions of the reinforcements the enemy has. If it's empty there are not reinforcements
        /// </summary>
        public List<SavedSquad> EnemyReinforcements;
        /// <summary>
        /// The friendly squad compositions of the reinforcements the user/friendly side has. If it's empty there are not reinforcements
        /// </summary>
        public List<SavedSquad> FriendlyReinforcements;
        /// <summary>
        /// The enemy squad compositions that the player will face
        /// </summary>
        public List<SavedSquad> EnemySquads;
        /// <summary>
        /// The already built enemy squads that will come into the level
        /// </summary>
        public List<int> EnemyExistingSquads;
        /// <summary>
        /// The basic report of the enemy forces that the player will face. This is shown to the player when they choose their squads
        /// </summary>
        public string EnemyReport;
        /// <summary>
        /// The squad compositions that the player chose
        /// </summary>
        public List<SavedSquad> ChosenSquads;
        /// <summary>
        /// The starting positions for the player ships and the AI ships. If it's empty then we use the map defaults
        /// </summary>
        public Vector2 UserStartingPosition, AIStartingPosition;


        public LevelOptions(int id, int side, string name, int mapIndex, string obstacles, List<(Vector2, Vector2)> obstacleList, int asteroidOption, int fogOfWar, int mining, bool hasPreLevelIntro, bool hasSquadActionBox, int supplyCapacity, int enemyReinforcementsOption, int enemyReinforcementDelay, int enemyShipTypeOption, int enemySquadGenerationCount, List<SavedSquad> enemyReinforcements, List<SavedSquad> enemySquads, List<int> enemyExistingSquads, string enemyReport, List<SavedSquad> chosenSquads, Vector2 userStartingPosition, Vector2 aiStartingPosition) 
        {
            Id = id;
            Side = side;
            Name = name;
            MapIndex = mapIndex;
            Obstacles = obstacles;
            ObstacleList = obstacleList;
            AsteroidOption = asteroidOption;
            FogOfWar = fogOfWar;
            Mining = mining;
            HasPreLevelIntro = hasPreLevelIntro;
            HasSquadActionBox = hasSquadActionBox;
            SupplyCapacity = supplyCapacity;
            EnemyReinforcementsOption = enemyReinforcementsOption;
            EnemyReinforcementDelay = enemyReinforcementDelay;
            EnemyShipTypeOption = enemyShipTypeOption;
            EnemySquadGenerationCount = enemySquadGenerationCount;
            EnemyReinforcements = enemyReinforcements;
            EnemySquads = enemySquads;
            EnemyExistingSquads = enemyExistingSquads;
            EnemyReport = enemyReport;
            ChosenSquads = chosenSquads;
            UserStartingPosition = userStartingPosition;
            AIStartingPosition = aiStartingPosition;
           
            //Debug.Log($"Creating level: {GetEnemyList()}");
        }

        public LevelOptions(int id, int side, string name)
        {
            Id = id;
            Side = side;
            Name = name;
            MapIndex = -1;
            Obstacles = "No";
            AsteroidOption = -1;
            FogOfWar = -1;
            Mining = -1;
            HasPreLevelIntro = false;
            EnemyReinforcementsOption = -1;
            EnemyReinforcements = new List<SavedSquad>();

            //FriendlyReinforcements = new List<SavedSquad>();
            ObstacleList = new List<(Vector2, Vector2)>();
            EnemySquads = new List<SavedSquad>();
            EnemyExistingSquads = new List<int>();
            ChosenSquads = new List<SavedSquad>();
        }

        public string ToJson()
        {
            JObject json = new JObject
            {
                ["Id"] = Id,
                ["Side"] = Side,
                ["Name"] = Name,
                ["MapIndex"] = MapIndex,
                ["Obstacles"] = Obstacles,
                ["AsteroidOption"] = AsteroidOption,
                ["FogOfWar"] = FogOfWar,
                ["Mining"] = Mining,
                ["HasPreLevelIntro"] = HasPreLevelIntro,
                ["HasSquadActionBox"] = HasSquadActionBox,
                ["SupplyCapacity"] = SupplyCapacity,
                ["EnemyReinforcementsOption"] = EnemyReinforcementsOption,
                ["EnemyReinforcementDelay"] = EnemyReinforcementDelay,
                ["EnemyReport"] = EnemyReport ?? string.Empty,
                ["UserStartingPosition"] = VectorToJson(UserStartingPosition),
                ["AIStartingPosition"] = VectorToJson(AIStartingPosition),
                ["EnemyReinforcements"] = new JArray(
                    EnemyReinforcements.Select(squad => JToken.Parse(squad.ToJson()))),
                ["EnemySquads"] = new JArray(
                    EnemySquads.Select(squad => JToken.Parse(squad.ToJson()))),
                ["EnemyExistingSquads"] = new JArray(EnemyExistingSquads),
                ["ObstacleList"] = new JArray(ObstacleList.Select(obstacle => new JObject
                {
                    ["Position"] = VectorToJson(obstacle.Item1),
                    ["Scale"] = VectorToJson(obstacle.Item2)
                }))
            };
            return json.ToString(Formatting.None);
        }

        private static JObject VectorToJson(Vector2 vector)
        {
            return new JObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y
            };
        }
        public override string ToString()
        {
            return $"Level #{Id} - {Name}";
        }

        public string GetEnemyList()
        {
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                return GetEnemyReport();
            }
            string enemyList = "Enemies: \n";
            List<ConfigData.ShipTypes> shipTypes = Side == ConfigData.Configuration.BeeSide ? ConfigData.UserProgressData.VisibleBeeShipTypes.ToList() : ConfigData.UserProgressData.VisibleHumanShipTypes.ToList();
            Dictionary<string, int> ships = new Dictionary<string, int>();



            shipTypes.ForEach((type) =>
            {
                enemyList += $"\t- {Utilities.ConvertShipTypeToName[type]}: {EnemySquads.Sum((squad) => squad.GetSquadShips().Where((ship) => ship.ShipType == type).Count())}\n";
            });

            //enemyList += "\nReinforcements: \n";

            //if (EnemyReinforcementsOption == 1)
            //{
            //    shipTypes.ForEach((type) =>
            //    {
            //        enemyList += $"\t- {Utilities.ConvertShipTypeToName[type]}: {EnemyReinforcements.Sum((squad) => squad.GetSquadShips().Where((ship) => ship.ShipType == type).Count())}\n";
            //    });
            //}
            //else
            //{
            //    enemyList += "None";
            //}

            return enemyList;
        }
        public string GetEnemyReport()
        {
            return $"Scout Report: {EnemyReport}";
        }
        public string GetLevelDetails()
        {
            return $"Map: {ConfigData.Maps[MapIndex].Name}\n" +
                $"Obstacles: {(Obstacles == "No" ? "No" : "Yes")}\n" +
                $"Asteroids: {(AsteroidOption == 0 ? "No" : (AsteroidOption == 1 ? "Yes" : "Tons"))}\n" +
                $"Fog of War: {(FogOfWar == 1 ? "Yes" : "No")}\n" +
                $"Mining: {(Mining == 1 ? "Yes": "No")}\n\n" +
                //$"Supply Capacity: {SupplyCapacity}\n\n" +
                GetEnemyList();
        }
        public string GetAllLevelDetails()
        {
            return GetLevelDetails()+"\n\n" +
                $"EnemySquadGenerationCount: {EnemySquadGenerationCount}\n" +
                $"Chosen Squads: {ChosenSquads.Count}\n";
        }
        public bool Equals(LevelOptions level)
        {
            return level.Id == Id;
        }
        public object Clone()
        {
            LevelOptions clone = (LevelOptions)MemberwiseClone();

            clone.EnemyReinforcements = new List<SavedSquad>();
            EnemyReinforcements.ForEach((squad) =>
            {
                clone.EnemyReinforcements.Add((SavedSquad)squad.Clone());
            });

            clone.EnemySquads = new List<SavedSquad>();
            EnemySquads.ForEach((squad) =>
            {
                clone.EnemySquads.Add((SavedSquad)squad.Clone());
            });

            clone.ChosenSquads = new List<SavedSquad>();
            ChosenSquads.ForEach((squad) =>
            {
                clone.ChosenSquads.Add((SavedSquad)squad.Clone());
            });

            return clone;
        }
    }
}
