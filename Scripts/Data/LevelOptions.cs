using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
        /// 1 = Uranus
        /// </summary>
        public int MapIndex;
        /// <summary>
        /// The index of the obstacle map that the level has
        /// -1 = Random
        /// 0 = No obstacles
        /// 1 = Maze
        /// 2 = Three Paths
        /// 3 = The Forest
        /// 4 = The Wall
        /// </summary>
        public int ObstacleMapIndex;
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
        /// The squad compositions that the player chose
        /// </summary>
        public List<SavedSquad> ChosenSquads;

        public LevelOptions(int id, int side, string name, int mapIndex, int obstacleMapIndex, int asteroidOption, int fogOfWar, int mining, int supplyCapacity, 
            int enemyReinforcementsOption, int enemyReinforcementDelay, int enemyShipTypeOption, int enemySquadGenerationCount, List<SavedSquad> enemyReinforcements,
             List<SavedSquad> enemySquads, List<SavedSquad> chosenSquads) 
        {
            Id = id;
            Side = side;
            Name = name;
            MapIndex = mapIndex;
            ObstacleMapIndex = obstacleMapIndex;
            AsteroidOption = asteroidOption;
            FogOfWar = fogOfWar;
            Mining = mining;
            SupplyCapacity = supplyCapacity;
            EnemyReinforcementsOption = enemyReinforcementsOption;
            EnemyReinforcementDelay = enemyReinforcementDelay;
            EnemyShipTypeOption = enemyShipTypeOption;
            EnemySquadGenerationCount = enemySquadGenerationCount;
            EnemyReinforcements = enemyReinforcements;
            EnemySquads = enemySquads;
            ChosenSquads = chosenSquads;
            Debug.Log($"Creating level: {GetEnemyList()}");
        }

        public LevelOptions(int id, int side, string name)
        {
            Id = id;
            Side = side;
            Name = name;
            MapIndex = -1;
            ObstacleMapIndex = -1;
            AsteroidOption = -1;
            FogOfWar = -1;
            Mining = -1;
            EnemyReinforcementsOption = -1;
            EnemyReinforcements = new List<SavedSquad>();
            //FriendlyReinforcements = new List<SavedSquad>();
            EnemySquads = new List<SavedSquad>();
            ChosenSquads = new List<SavedSquad>();
        }

        public string ToJson()
        {
            string json = $"{{\"Id\": {Id}, \"Side\": {Side}, \"Name\": \"{Name}\", \"MapIndex\": {MapIndex}, \"ObstacleMapIndex\": {ObstacleMapIndex}, \"AsteroidOption\": {AsteroidOption}, " +
                $"\"FogOfWar\": {FogOfWar}, \"Mining\": {Mining}, \"SupplyCapacity\": {SupplyCapacity}, \"EnemyReinforcementsOption\": {EnemyReinforcementsOption}," +
                $" \"EnemyReinforcementDelay\": {EnemyReinforcementDelay}, \"EnemyReinforcements\": [";
            
            if (EnemyReinforcements.Count > 0)
            {
                EnemyReinforcements.ForEach((s) => json += $"{s.ToJson()}, ");
                json = json.Remove(json.Length - 2);
            }

            json += "], \"EnemySquads\": [";
            if (EnemySquads.Count > 0)
            {
                EnemySquads.ForEach((s) => json += $"{s.ToJson()}, ");
                json = json.Remove(json.Length - 2);
            }


            json += "]}";
            return json;
        }
        public override string ToString()
        {
            return $"Level #{Id} - {Name}";
        }

        public string GetEnemyList()
        {
            string enemyList = "Enemies: \n";
            List<ConfigData.ShipTypes> shipTypes = Side == ConfigData.Configuration.BeeSide ? ConfigData.Configuration.VisibleBeeShipTypes.ToList() : ConfigData.Configuration.VisibleHumanShipTypes.ToList();
            Dictionary<string, int> ships = new Dictionary<string, int>();



            shipTypes.ForEach((type) =>
            {
                enemyList += $"\t- {type}: {EnemySquads.Sum((squad) => squad.GetSquadShips().Where((ship) => ship.ShipType == type).Count())}\n";
            });

            enemyList += "\nReinforcements: \n";

            if (EnemyReinforcementsOption == 1)
            {
                shipTypes.ForEach((type) =>
                {
                    enemyList += $"\t- {type}: {EnemyReinforcements.Sum((squad) => squad.GetSquadShips().Where((ship) => ship.ShipType == type).Count())}\n";
                });
            }
            else
            {
                enemyList += "None";
            }

            return enemyList;
        }
        public string GetLevelDetails()
        {
            return $"Map: {ConfigData.Maps[MapIndex]}\n" +
                $"Obstacles: {ConfigData.ObstacleMaps[ObstacleMapIndex]}\n" +
                $"Asteroids: {(AsteroidOption == 0 ? "None" : (AsteroidOption == 1 ? "Yes" : "Double"))}\n" +
                $"Fog of War: {(FogOfWar == 1 ? "Yes" : "No")}\n" +
                $"Mining: {(Mining == 1 ? "Yes": "No")}\n" +
                $"Supply Capacity: {SupplyCapacity}\n\n" +
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