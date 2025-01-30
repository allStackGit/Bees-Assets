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

            BeeSmallLaserShotPrefab, BeeMediumLaserShotPrefab, BumblebeeShotPrefab, FlagshipShotPrefab, RocketPrefab, HumanSmallPrefab, HumanMediumPrefab, BeamPrefab,
            SplitShotPrefab, QueenSmallPrefab, QueenLargePrefab, StrikerBombPrefab, RocketExplosionPrefab, FireBargeExplosionPrefab,
            
            BeeSmallProjectileExplosionAnimationPrefab, BeeMediumProjectileExplosionAnimationPrefab, BumblebeeShotProjectileExplosionAnimationPrefab, FlagshipShotProjectileExplosionAnimationPrefab,
            HumanSmallProjectileExplosionAnimationPrefab, HumanMediumProjectileExplosionAnimationPrefab, SplitShotProjectileExplosionAnimationPrefab, QueenSmallProjectileExplosionAnimationPrefab,
            QueenLargeProjectileExplosionAnimationPrefab, StrikerBombProjectileExplosionAnimationPrefab,

            BargeRemainsPrefab, BeehiveRemainsPrefab, BumblebeeRemainsPrefab, CarpenterBeeRemainsPrefab, CarrierRemainsPrefab, CruiserRemainsPrefab, DreadnoughtRemainsPrefab, DroneRemainsPrefab,
    FactoryRemainsPrefab, FireBargeRemainsPrefab, FlagshipRemainsPrefab, FrigateRemainsPrefab, GunshipRemainsPrefab, HoneybeeRemainsPrefab, HornetRemainsPrefab, LeafcutterRemainsPrefab, QueenRemainsPrefab,
    ScoutRemainsPrefab, StrikerRemainsPrefab, WarpGateRemainsPrefab, WaspRemainsPrefab, YellowJacketRemainsPrefab, BeaconRemainsPrefab,
            
            TinyShipExplosionPrefab, SmallShipExplosionPrefab, MediumShipExplosionPrefab, LargeShipExplosionPrefab, HugeShipExplosionPrefab, BeehiveShipExplosionPrefab, QueenShipExplosionPrefab;
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
        public Dictionary<ConfigData.ProjectileTypes, GameObject> ConvertProjectileTypeToExplosionAnimation;
        public Dictionary<ConfigData.ShipTypes, GameObject> ConvertShipTypeToRemainsPrefab;
        public Dictionary<ConfigData.ShipTypes, GameObject> ConvertShipTypeToExplosionPrefab;

        public void LoadConversions()
        {
            ConvertProjectileTypeToExplosionAnimation = new Dictionary<ConfigData.ProjectileTypes, GameObject>
            {
                { ConfigData.ProjectileTypes.BeeSmall, BeeSmallProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.BeeMedium, BeeMediumProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.BumblebeeShot, BumblebeeShotProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.FlagshipShot, FlagshipShotProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.Rocket, RocketExplosionPrefab },
                { ConfigData.ProjectileTypes.HumanSmall, HumanSmallProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.HumanMedium, HumanMediumProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.SplitShot, SplitShotProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.QueenSmall, QueenSmallProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.QueenLarge, QueenLargeProjectileExplosionAnimationPrefab },
                { ConfigData.ProjectileTypes.StrikerBomb, StrikerBombProjectileExplosionAnimationPrefab },
            };

            ConvertShipTypeToRemainsPrefab = new Dictionary<ConfigData.ShipTypes, GameObject>
            {
                { ConfigData.ShipTypes.Drone, DroneRemainsPrefab },
                { ConfigData.ShipTypes.Gunship, GunshipRemainsPrefab },
                { ConfigData.ShipTypes.Honeybee, HoneybeeRemainsPrefab },
                { ConfigData.ShipTypes.Hornet, HornetRemainsPrefab },
                { ConfigData.ShipTypes.Scout, ScoutRemainsPrefab },
                { ConfigData.ShipTypes.Striker, StrikerRemainsPrefab }, 
                { ConfigData.ShipTypes.YellowJacket, YellowJacketRemainsPrefab },
            };

            ConvertShipTypeToExplosionPrefab = new Dictionary<ConfigData.ShipTypes, GameObject>
            {
                { ConfigData.ShipTypes.Beacon, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Drone, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Hornet, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.YellowJacket, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Striker, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Scout, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Honeybee, TinyShipExplosionPrefab },

                { ConfigData.ShipTypes.Gunship, SmallShipExplosionPrefab },
                { ConfigData.ShipTypes.Wasp, SmallShipExplosionPrefab },
                { ConfigData.ShipTypes.Frigate, SmallShipExplosionPrefab },

                { ConfigData.ShipTypes.Dreadnought, MediumShipExplosionPrefab },
                { ConfigData.ShipTypes.Leafcutter, MediumShipExplosionPrefab },
                { ConfigData.ShipTypes.Cruiser, MediumShipExplosionPrefab },

                { ConfigData.ShipTypes.Bumblebee, LargeShipExplosionPrefab },
                { ConfigData.ShipTypes.Carrier, LargeShipExplosionPrefab },

                { ConfigData.ShipTypes.Barge, HugeShipExplosionPrefab },
                { ConfigData.ShipTypes.CarpenterBee, HugeShipExplosionPrefab },
                { ConfigData.ShipTypes.Flagship, HugeShipExplosionPrefab },
                { ConfigData.ShipTypes.WarpGate, HugeShipExplosionPrefab },
                { ConfigData.ShipTypes.Factory, HugeShipExplosionPrefab },

                { ConfigData.ShipTypes.FireBarge, FireBargeExplosionPrefab },

                { ConfigData.ShipTypes.Beehive, TinyShipExplosionPrefab },
                { ConfigData.ShipTypes.Queen, TinyShipExplosionPrefab },

            };
        }

    }
}
