using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Assets.Scripts.Levels
{
    public class Prefabs : MonoBehaviour
    {
        /// <summary>
        /// Level Prefab
        /// </summary>
        public Level LevelPrefab;
        /// <summary>
        /// Ship Prefabs
        /// </summary>
        public GameObject BargePrefab, BeehivePrefab, BumblebeePrefab, CarpenterBeePrefab, CarrierPrefab, CruiserPrefab, DreadnoughtPrefab, DronePrefab,
    FactoryPrefab, FireBargePrefab, FlagshipPrefab, FrigatePrefab, GunshipPrefab, HoneybeePrefab, HornetPrefab, LeafcutterPrefab, QueenPrefab,
    ScoutPrefab, StrikerPrefab, WarpGatePrefab, WaspPrefab, YellowJacketPrefab, BeaconPrefab,
            
            BeeMediumLaserShotPrefab;
        /// <summary>
        /// UI Prefabs
        /// </summary>
        public GameObject MovementMarkerPrefab, TargetingMarkerPrefab,
    SquadBoxPrefab;
        /// <summary>
        /// Obstacle prefabs for each set of obstacles
        /// </summary>
        public List<GameObject> EmptyObstacleList, MazePrefabs, ThreePathsPrefabs, ForestPrefabs, TheWallPrefabs = new List<GameObject>();

        public List<GameObject> MiningAsteroidPrefabs = new List<GameObject>();
        public List<GameObject> CollisionAsteroidPrefabs = new List<GameObject>();
        /// <summary>
        /// Asteroids that can potentially be spawned from larger asteroids breaking apart
        /// </summary>
        public List<GameObject> BreakawayAsteroids = new List<GameObject>();
        /// <summary>
        /// Asteroid pieces that spawn (and don't collide) from larger asteroids breaking apart
        /// </summary>
        public List<GameObject> AsteroidPieces = new List<GameObject>();
        /// <summary>
        /// A list of possible maps to load
        /// </summary>
        public List<GameObject> Maps = new List<GameObject>();

    }
}
