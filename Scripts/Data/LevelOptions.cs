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
        /// The amount of time (in seconds) before enemy reinforcement spawn outside of the map. A time of 0 indicates no reinforcements
        /// </summary>
        public int EnemyReinforcementDelay;
        /// <summary>
        /// The enemy squad compositions of the reinforcements the enemy has. If it's empty there are not reinforcements
        /// </summary>
        public List<SavedSquad> EnemyReinforcements;
        /// <summary>
        /// The enemy squad compositions that the player will face
        /// </summary>
        public List<SavedSquad> EnemySquads;

        public LevelOptions(int id, int side, string name, int mapIndex, int obstacleMapIndex, int asteroidOption, int fogOfWar, int mining, int supplyCapacity, int enemyReinforcementDelay, List<SavedSquad> enemyReinforcements,
             List<SavedSquad> enemySquads) 
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
            EnemyReinforcementDelay = enemyReinforcementDelay;
            EnemyReinforcements = enemyReinforcements;
            EnemySquads = enemySquads;
        }

        public string ToJson()
        {
            string json = $"{{\"Id\": {Id}, \"Side\": {Side}, \"Name\": \"{Name}\", \"MapIndex\": {MapIndex}, \"ObstacleMapIndex\": {ObstacleMapIndex}, \"AsteroidOption\": {AsteroidOption}, " +
                $"\"FogOfWar\": {FogOfWar}, \"Mining\": {Mining}, \"SupplyCapacity\": {SupplyCapacity}, \"EnemyReinforcementDelay\": {EnemyReinforcementDelay}, \"EnemyReinforcements\": [";
            
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
            List<string> shipTypes = Side == ConfigData.Configuration.BeeSide ? ConfigData.Configuration.VisibleBeeShipTypes.ToList() : ConfigData.Configuration.VisibleHumanShipTypes.ToList();
            Dictionary<string, int> ships = new Dictionary<string, int>();



            shipTypes.ForEach((type) =>
            {
                enemyList += $"\t- {type}: {EnemySquads.Sum((squad) => squad.GetSquadShips().Where((ship) => ship.ShipType == type).Count())}\n";
            });

            enemyList += "\nReinforcements: \n";

            if (EnemyReinforcements.Count > 0)
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
                clone.EnemyReinforcements.Add(squad);
            });

            clone.EnemySquads = new List<SavedSquad>();
            EnemySquads.ForEach((squad) =>
            {
                clone.EnemySquads.Add(squad);
            });

            return clone;
        }
    }
}