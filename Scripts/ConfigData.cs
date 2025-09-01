using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
using UnityEngine.UI;            

namespace Assets.Scripts
{
    // variables that should be accessible across scenes
    public static class ConfigData
    {
        // server and socket settings
        /// <summary>
        /// Test is for beta testing, non-local. Development is for development, local.
        /// </summary>
        public const bool Test = true; // [alert] should be true for beta testing
        public const bool Development = false;
        public const bool Production = !Test && !Development;
        public const string LocalServerHostname = "192.168.36.2";
        public const string GlobalServerHostname = "seagrams7.softether.net";
        public const string TestServerHostname = GlobalServerHostname;
        public const string DevelopmentServerHostname = LocalServerHostname;
        public const string ProductionServerHostname = GlobalServerHostname;
        public const int DevelopmentPort = 7146;
        public const int TestPort = 7143;
        public const int ProductionPort = 7144;
        public const int RLPort = 7242;
        public const int StandardMaxTimeOnQueue = 10;

        public const int ObstaclesLayerMask = 1 << 19; // the layer masks need to all be 1 and then bitwise shifted to the left by the layer number
        public const int ObstacleProximityRangesLayerMask = 1 << 20;
        public const int BeeShipsLayerMask = 1 << 11;

        public const int FogOfWarLayer = 21;
        public const int VisionRangesLayer = 22;

        public static Configuration Configuration;
        public static StartingSettings StartingSettings;
        public static ShipStats ShipInfo;


        // 2 = standard testing,
        // 3 = standard beta,
        // 4 = bennett beta,
        // 5 = ml agents rl testing,
        // 6 = standard testing [highest trained]
        // 7 = new NN training version
        public const int Version = 5; // [alert] should be increased when released
        public const string BaseFolder = "SaveData";
        public const string CacheFolder = "SpriteCache";
        public const string PortraitFolder = "Sprites/People";

        // constant filenames
        public const string UserProgressFilename = "user_progress";
        public static string[] FleetDataFilenames = new string[] { "campaign_fleet_data", "fleet_data" };
        public static string[] SavedSquadsDataFilenames = new string[] { "campaign_saved_squads_data", "saved_squads_data" };
        public const string UserSettingsFilename = "user_settings_data";
        public static string[] LevelsDataFilenames = new string[] {"campaign_levels_data", "levels_data" };


        // data loaded booleans
        public static bool IsUserProgressDataLoaded, IsUserSettingsDataLoaded;
        public static bool[] IsLevelsDataLoaded = new bool[] { false, false };
        public static bool[] IsSavedSquadsDataLoaded = new bool[] { false, false };
        public static bool[] IsFleetDataLoaded = new bool[] { false, false };

        public enum SceneTypes
        {
            Scene,
            Stage
        }
        public enum SquadTypes
        {
            Squad,
            CarrierSquad
        }
        public enum ShipTypes
        {
            Barge,
            Beacon,
            Beehive,
            Bumblebee,
            CarpenterBee,
            Carrier,
            Cruiser,
            Dreadnought,
            Drone,
            Factory,
            FireBarge,
            Flagship,
            Frigate,
            Gunship,
            Honeybee,
            Hornet,
            Leafcutter,
            Queen,
            Scout,
            Striker,
            WarpGate,
            Wasp,
            YellowJacket,
        }
        public enum ShipTypeLetters
        {
            A,
            B,
            C,
            D,
            E,
            F,
            G,
            H,
            I,
            J,
            K,
            L,
            M,
            N,
            O,
            P,
            Q,
            R,
            S,
            T,
            U,
            V,
            W
        }
        public enum WeaponTypes
        {
            Bomb,
            BeamCannon,
            LightCannon,
            Turret,
            FullShipTurret,
            RocketTurret,
            DualCannon,
            Eye,
            QueenEye,
            SplitShot,
        }
        public enum WeaponSoundTypes
        {
            SmallLaser, // Drone, Hornet, Gunship, Queen Turret, Flagship Turret,
            BigLaser, //  Wasp, Leafcutter,
            FlagshipChargingLaser, // Flagship
            FlagshipLaser, // Flagship
            QueenLaser, // Queen Crown
            BeamCannon, // Cruiser
            BowtieLaser, // Bumblebee
            LightCannon, // Dreadnought
            RocketLaunch, // Frigate
            Bomb, // Striker
            FireBargeBomb, // Fire Barge
            None,

        }
        public enum RequestTypes
        {
            Request, // Not a real type
            GetMatchupStrategy,
            GetStrategy,
            SendRLData,
            StoreCommands,
            SetupLevel,
            ReconnectLevel,
            StoreUserData,
            GetUserData,
            GetSettings
        }

        public enum ProjectileTypes
        {
            None,
            BeeSmall,
            BeeMedium,
            BumblebeeShot,
            FlagshipShot,
            Rocket,
            HumanSmall,
            HumanMedium,
            Beam,
            SplitShot,
            QueenSmall,
            QueenLarge,
            StrikerBomb,
            RocketExplosion,
            FireBargeExplosion
        }
        public enum CommandTypes
        {
            Uninitialized, // Not an actual command 0
            Matchup, // Not an actual command 1
            Shooting, // Not an actual command 2
            Aggressive, // 3
            BombingRun, // 4
            Charge, // 5
            Retreat, // 6
            MoveToRandom, // 7
            CircleSquad, // 8
            RightSwipe, // 9
            LeftSwipe, // 10
            ClosestFriendly, // 11
            InAndOut, // 12
            Patrol, // 13
            Guard, // 14
            Scouting, // 15
            Mining, // 16
            FullRetreat, // 17
            Hold, // 18
            Heal, // 19
            MoveToPoint, // 20
        }

        public enum ShootingStrategyTypes
        {
            FirstSeen, // 0
            Random,
            Revenge,
            MostDangerous,
            MostHealth,
            LeastHealth,
            MostPowerful,
            LeastPowerful,
            Closest,
            Furthest,
            MostRange,
            LeastRange,
            Fastest,
            Slowest,
            MostValuable,
            LeastValuable, // 15
            TypeA,
            TypeB,
            TypeC,
            TypeD,
            TypeE,
            TypeF,
            TypeG,
            TypeH,
            TypeI,
            TypeJ,
            TypeK,
            TypeL,
            TypeM,
            TypeN,
            TypeO,
            TypeP,
            TypeQ,
            TypeR,
            TypeS,
            TypeT,
            TypeU,
            TypeV,
            TypeW,
        }

        public enum SquadActions
        {
            IsMatchingSpeed,
            CeaseFire,
            AttackOnSight,
            Patrol,
            Guard,
            Chase,
            Hold,
        }
        public enum ObstacleTypes
        {
            StaticObstacle,
            MapBorder,
            CollisionAsteroid,
            MiningAsteroid,
            AsteroidPiece
        }
        public enum Locations
        {
            Pluto,
            Neptune,
            Uranus
        }

        public enum GameModes
        {
            Campaign,
            FreePlay,
            Challenge,
        }

        public enum MatchupStrategyTypes
        {
            Random,
            Revenge,
            MostDangerous,
            LeastHealth,
            MostHealth,
            MostPowerful,
            LeastPowerful,
            Closest,
            Furthest,
            MostRange,
            LeastRange,
            Fastest,
            Slowest,
            InCombat,
            GangUp,
            MostValuable,
            LeastValuable,
            TypeA,
            TypeB,
            TypeC,
            TypeD,
            TypeE,
            TypeF,
            TypeG,
            TypeH,
            TypeI,
            TypeJ,
            TypeK,
            TypeL,
            TypeM,
            TypeN,
            TypeO,
            TypeP,
            TypeQ,
            TypeR,
            TypeS,
            TypeT,
            TypeU,
            TypeV,
            TypeW
        }

        
        /// <summary>
        /// Size class 3 in the spreeadsheet
        /// </summary>
        public const int Tiny = 1; // this is the base size, equal to 4 World Units
        /// <summary>
        /// Size class 5 in the spreeadsheet
        /// </summary>
        public const float Small = 1.5f;
        /// <summary>
        /// Size class 6 in the spreeadsheet
        /// </summary>
        public const int Medium = 2;
        /// <summary>
        /// Size class 7 in the spreeadsheet
        /// </summary>
        public const int Large = 3;
        /// <summary>
        /// Size class 8 in the spreeadsheet
        /// </summary>
        public const int Huge = 4;
        /// <summary>
        /// Size class 9 in the spreeadsheet
        /// </summary>
        public const int Enormous = 8;
        /// <summary>
        /// Size class 10 in the spreeadsheet
        /// </summary>
        public const int Unfathomable = 32;

        public static readonly Dictionary<ShipTypes, Vector2Int> ShipSizes = new Dictionary<ShipTypes, Vector2Int>() {
            { ShipTypes.Barge,          new Vector2Int(152, 72)},
            { ShipTypes.Beacon,         new Vector2Int(18, 16)},
            { ShipTypes.Carrier,        new Vector2Int(96, 112)},
            { ShipTypes.Cruiser,        new Vector2Int(64, 72)},
            { ShipTypes.Dreadnought,    new Vector2Int(64, 84)},
            { ShipTypes.Drone,          new Vector2Int(32, 32)},
            { ShipTypes.Factory,        new Vector2Int(128, 128)},
            { ShipTypes.FireBarge,      new Vector2Int(152, 72)},
            { ShipTypes.Flagship,       new Vector2Int(128, 152)},
            { ShipTypes.Frigate,        new Vector2Int(48, 48)},
            { ShipTypes.Gunship,        new Vector2Int(48, 48)},
            { ShipTypes.Scout,          new Vector2Int(40, 32)},
            { ShipTypes.Striker,        new Vector2Int(32, 32)},
            { ShipTypes.WarpGate,       new Vector2Int(224, 128)},

            { ShipTypes.Beehive,        new Vector2Int(272, 272)},
            { ShipTypes.Bumblebee,      new Vector2Int(136, 96)},
            { ShipTypes.CarpenterBee,   new Vector2Int(128, 128)},
            { ShipTypes.Honeybee,       new Vector2Int(32, 32)},
            { ShipTypes.Hornet,         new Vector2Int(32, 32)},
            { ShipTypes.Leafcutter,     new Vector2Int(64, 64)},
            { ShipTypes.Queen,          new Vector2Int(1280, 1024)},
            { ShipTypes.Wasp,           new Vector2Int(48, 48)},
            { ShipTypes.YellowJacket,   new Vector2Int(32, 32)},

        };

        public static readonly Dictionary<ShipTypes, Vector2Int> ShipRemainsSizes = new Dictionary<ShipTypes, Vector2Int>() {
            { ShipTypes.Barge,         new Vector2Int(254, 254)},
            { ShipTypes.Beacon,        new Vector2Int(0, 0)},
            { ShipTypes.Carrier,       new Vector2Int(198, 198)},
            { ShipTypes.Cruiser,       new Vector2Int(158, 158)},
            { ShipTypes.Dreadnought,   new Vector2Int(134, 134)},
            { ShipTypes.Drone,         new Vector2Int(86, 86)},
            { ShipTypes.Factory,       new Vector2Int(238, 238)},
            { ShipTypes.FireBarge,     new Vector2Int(0, 0)},
            { ShipTypes.Flagship,      new Vector2Int(210, 238)},
            { ShipTypes.Frigate,       new Vector2Int(100, 100)},
            { ShipTypes.Gunship,       new Vector2Int(100, 100)},
            { ShipTypes.Scout,         new Vector2Int(80, 80)},
            { ShipTypes.Striker,       new Vector2Int(78, 78)},
            { ShipTypes.WarpGate,      new Vector2Int(382, 238)},

            { ShipTypes.Beehive,       new Vector2Int(0, 0)},
            { ShipTypes.Bumblebee,     new Vector2Int(0, 0)},
            { ShipTypes.CarpenterBee,  new Vector2Int(0, 0)},
            { ShipTypes.Honeybee,      new Vector2Int(0, 0)},
            { ShipTypes.Hornet,        new Vector2Int(0, 0)},
            { ShipTypes.Leafcutter,    new Vector2Int(0, 0)},
            { ShipTypes.Queen,         new Vector2Int(0, 0)},
            { ShipTypes.Wasp,          new Vector2Int(0, 0)},
            { ShipTypes.YellowJacket,  new Vector2Int(0, 0)},
        };
        public static readonly Dictionary<ShipTypes, float> ShipSizeFactor = new Dictionary<ShipTypes, float>() {
            { ShipTypes.Barge,         Huge},
            { ShipTypes.Beacon,        Tiny},
            { ShipTypes.Carrier,       Large},
            { ShipTypes.Cruiser,       Medium},
            { ShipTypes.Dreadnought,   Medium},
            { ShipTypes.Drone,         Tiny},
            { ShipTypes.Factory,       Huge},
            { ShipTypes.FireBarge,     Huge},
            { ShipTypes.Flagship,      Huge},
            { ShipTypes.Frigate,       Small},
            { ShipTypes.Gunship,       Small},
            { ShipTypes.Scout,         Tiny},
            { ShipTypes.Striker,       Tiny},
            { ShipTypes.WarpGate,      Huge},

            { ShipTypes.Beehive,       Enormous},
            { ShipTypes.Bumblebee,     Large},
            { ShipTypes.CarpenterBee,  Huge},
            { ShipTypes.Honeybee,      Tiny},
            { ShipTypes.Hornet,        Tiny},
            { ShipTypes.Leafcutter,    Medium},
            { ShipTypes.Queen,         Unfathomable},
            { ShipTypes.Wasp,          Small},
            { ShipTypes.YellowJacket,  Tiny},
        };
        public static readonly Dictionary<ShipTypes, Color[]> ChangeableShipColors = new Dictionary<ShipTypes, Color[]>() {
            { ShipTypes.Barge, new Color[] {
                new Color(0.235f, 0.753f, 0.498f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1), 
                new Color(0.196f, 0.6f, 0.4f, 1),
                new Color(0.1098f, 0.3568f, 0.2352f, 1)
            } },
            { ShipTypes.Beacon, new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { ShipTypes.Carrier, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1),
                new Color(0.161f, 0.510f, 0.337f, 1),
                new Color(0.1098f, 0.3568f, 0.2352f, 1)
            } },
            { ShipTypes.Cruiser, new Color[] {
                new Color(0.184f, 0.569f, 0.380f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1), 
                new Color(0.196f, 0.6f, 0.4f, 1),
                new Color(0.1098f, 0.3568f, 0.2352f, 1)
            } },
            { ShipTypes.Dreadnought, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1), 
                new Color(0.1176f, 0.3725f, 0.2470f, 1)
            } },
            { ShipTypes.Drone, new Color[] {new Color(.729f, .729f, .729f, 1) } },
            //{ ShipTypes.Factory, new Color[] { 
            //    new Color(0.161f, 0.510f, 0.337f, 1), 
            //    new Color(0.196f, 0.6f, 0.4f, 1),
            //    new Color(0.1176f, 0.3725f, 0.2470f, 1),
            //    new Color(0.1098f, 0.3568f, 0.2352f, 1),
            //    new Color(0.0509f, 0.1607f, 0.1058f, 1),
            //} },
            { ShipTypes.Factory, new Color[] {
                new Color(0.196f, 0.6f, 0.4f, 1),
                new Color(0.161f, 0.510f, 0.337f, 1),
                new Color(0.1176f, 0.3725f, 0.2470f, 1)
            } },
            { ShipTypes.FireBarge, new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.235f, 0.753f, 0.498f, 1) } },
            { ShipTypes.Flagship, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1),
                new Color(0.1098f, 0.3568f, 0.2352f, 1),
            } },
            { ShipTypes.Frigate, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1),
                new Color(0.1058f, 0.3607f, 0.2352f, 1),
                new Color(0.1607f, 0.5411f, 0.3529f, 1),
                new Color(0.0941f, 0.3019f, 0.2235f, 1),
                new Color(0.1607f, 0.4431f, 0.2901f, 1),
                new Color(0.1294f, 0.3176f, 0.2235f, 1),
            } },


            { ShipTypes.Gunship, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1), 
                new Color(0.1843f, 0.5686f, 0.3725f, 1),
                new Color(0.1607f, 0.4823f, 0.3215f, 1), 
                new Color(0.1921f, 0.6039f, 0.3960f, 1), 
                new Color(0.1607f, 0.5098f, 0.3215f, 1),
                new Color(0.1450f, 0.4588f, 0.2941f, 1), 
                new Color(0.1921f, 0.6039f, 0.3882f, 1), 
                new Color(0.1921f, 0.5882f, 0.3882f, 1), 
                new Color(0.1921f, 0.5568f, 0.3882f, 1),
                new Color(0.1921f, 0.5882f, 0.4196f, 1), 
                new Color(0.2f, 0.5607f, 0.3803f, 1),  
                new Color(0.1647f, 0.5254f, 0.3450f, 1),
            } },


            { ShipTypes.Scout, new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1843f, 0.5686f, 0.3725f, 1),
                new Color(0.1607f, 0.4823f, 0.3215f, 1), new Color(0.1921f, 0.6039f, 0.3960f, 1), new Color(0.1607f, 0.5098f, 0.3215f, 1),
            new Color(0.1450f, 0.4588f, 0.2941f, 1), new Color(0.1921f, 0.6039f, 0.3882f, 1), new Color(0.1921f, 0.5882f, 0.3882f, 1), new Color(0.1921f, 0.5568f, 0.3882f, 1),
            new Color(0.1921f, 0.5882f, 0.4196f, 1), } },


            { ShipTypes.Striker, new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { ShipTypes.WarpGate, new Color[] { 
                new Color(0.196f, 0.6f, 0.4f, 1), 
                new Color(0.161f, 0.510f, 0.337f, 1),
                new Color(0.1098f, 0.3568f, 0.2352f, 1)
            } },

            // Set the bees to the unset color because none of their colors will change ... Unless the player is the bees?
            { ShipTypes.Beehive,        new Color[] {UnsetColor } },
            { ShipTypes.Bumblebee,      new Color[] {UnsetColor } },
            { ShipTypes.CarpenterBee,   new Color[] {UnsetColor } },
            { ShipTypes.Honeybee,       new Color[] {UnsetColor } },
            { ShipTypes.Hornet,         new Color[] {UnsetColor } },
            { ShipTypes.Leafcutter,     new Color[] {UnsetColor } },
            { ShipTypes.Queen,          new Color[] {UnsetColor } },
            { ShipTypes.Wasp,           new Color[] {UnsetColor } },
            { ShipTypes.YellowJacket,   new Color[] {UnsetColor } },
        };

        /// <summary>
        /// Offset in world units from the front of a ship when aiming at the front of a ship. Bigger is closer to center.
        /// </summary>
        public static readonly Dictionary<ShipTypes, float> OffsetFromFrontOfShip = new Dictionary<ShipTypes, float>()
        {
            { ShipTypes.Barge, .35f },
            { ShipTypes.Beacon, .35f },
            { ShipTypes.Carrier, .35f },
            { ShipTypes.Cruiser, .35f },
            { ShipTypes.Dreadnought, .35f },
            { ShipTypes.Drone, .35f },
            { ShipTypes.Factory, 1.35f },
            { ShipTypes.FireBarge, .35f },
            { ShipTypes.Flagship, .35f },
            { ShipTypes.Frigate, .35f },
            { ShipTypes.Gunship, .35f },
            { ShipTypes.Scout, .35f },
            { ShipTypes.Striker, .35f },
            { ShipTypes.WarpGate, 13f },

            { ShipTypes.Beehive, 4f },
            { ShipTypes.Bumblebee, 1.35f },
            { ShipTypes.CarpenterBee, 1.55f },
            { ShipTypes.Honeybee, .35f },
            { ShipTypes.Hornet, .35f },
            { ShipTypes.Leafcutter, .35f },
            { ShipTypes.Queen, .35f },
            { ShipTypes.Wasp, .55f },
            { ShipTypes.YellowJacket, .55f },
        };

        public static readonly HashSet<CommandTypes> TypesOfCommands = new HashSet<CommandTypes> { CommandTypes.Aggressive, CommandTypes.BombingRun, CommandTypes.Charge,
            CommandTypes.Retreat, CommandTypes.MoveToRandom, CommandTypes.CircleSquad, CommandTypes.RightSwipe, CommandTypes.LeftSwipe, CommandTypes.ClosestFriendly, CommandTypes.InAndOut,
            CommandTypes.Patrol, CommandTypes.Guard, CommandTypes.Scouting, CommandTypes.Mining, CommandTypes.FullRetreat, CommandTypes.Hold, CommandTypes.Heal };

        public static readonly List<ShootingStrategyTypes> TypesOfShootingStrategies = new List<ShootingStrategyTypes>
        {
            ShootingStrategyTypes.FirstSeen, // 0
            ShootingStrategyTypes.Random,
            ShootingStrategyTypes.Revenge,
            ShootingStrategyTypes.MostDangerous,
            ShootingStrategyTypes.MostHealth,
            ShootingStrategyTypes.LeastHealth,
            ShootingStrategyTypes.MostPowerful,
            ShootingStrategyTypes.LeastPowerful,
            ShootingStrategyTypes.Closest,
            ShootingStrategyTypes.Furthest,
            ShootingStrategyTypes.MostRange,
            ShootingStrategyTypes.LeastRange,
            ShootingStrategyTypes.Fastest,
            ShootingStrategyTypes.Slowest,
            ShootingStrategyTypes.MostValuable,
            ShootingStrategyTypes.LeastValuable, // 15
            ShootingStrategyTypes.TypeA,
            ShootingStrategyTypes.TypeB,
            ShootingStrategyTypes.TypeC,
            ShootingStrategyTypes.TypeD,
            ShootingStrategyTypes.TypeE,
            ShootingStrategyTypes.TypeF,
            ShootingStrategyTypes.TypeG,
            ShootingStrategyTypes.TypeH,
            ShootingStrategyTypes.TypeI,
            ShootingStrategyTypes.TypeJ,
            ShootingStrategyTypes.TypeK,
            ShootingStrategyTypes.TypeL,
            ShootingStrategyTypes.TypeM,
            ShootingStrategyTypes.TypeN,
            ShootingStrategyTypes.TypeO,
            ShootingStrategyTypes.TypeP,
            ShootingStrategyTypes.TypeQ,
            ShootingStrategyTypes.TypeR,
            ShootingStrategyTypes.TypeS,
            ShootingStrategyTypes.TypeT,
            ShootingStrategyTypes.TypeU,
            ShootingStrategyTypes.TypeV,
            ShootingStrategyTypes.TypeW,
        };

        public static ShootingStrategyTypes DefaultShootingStrategy = ShootingStrategyTypes.FirstSeen;

        public static HashSet<string> ShootingStrategyNames = new HashSet<string>
        {
            "First Seen", "Random", "Revenge", "Most Dangerous", "Most Health", "Least Health", "Most Powerful", "Least Powerful", "Closest", "Furthest",
 "Most Range", "Least Range", "Fastest",
 "Slowest", "Most Valuable", "Least Valuable", "Type A", "Type B", "Type C", "Type D", "Type E", "Type F", "Type G", "Type H", "Type I", "Type J", "Type K", "Type L",
 "Type M", "Type N", "Type O", "Type P", "Type Q", "Type R", "Type S", "Type T", "Type U", "Type V, Type W"
        };

        public static HashSet<ShipTypes> BeeShipTypes = new HashSet<ShipTypes>();
        public static HashSet<ShipTypes> HumanShipTypes = new HashSet<ShipTypes>();
        public static readonly HashSet<ShipTypes> BeeSwarmShips = new HashSet<ShipTypes> { ShipTypes.Honeybee, ShipTypes.Hornet, ShipTypes.YellowJacket };
        public static readonly HashSet<ShipTypes> SmallShips = new HashSet<ShipTypes> { ShipTypes.Honeybee, ShipTypes.Hornet, ShipTypes.YellowJacket, ShipTypes.Scout, 
            ShipTypes.Gunship, ShipTypes.Drone, ShipTypes.Wasp, ShipTypes.Striker, ShipTypes.Beacon, ShipTypes.Frigate  };
        public static readonly HashSet<ShipTypes> MediumShips = new HashSet<ShipTypes> { ShipTypes.Cruiser, ShipTypes.Dreadnought, ShipTypes.Leafcutter  };
        public static readonly HashSet<ShipTypes> LargeShips = new HashSet<ShipTypes> { ShipTypes.Queen, ShipTypes.Flagship, ShipTypes.Barge, ShipTypes.FireBarge, ShipTypes.Bumblebee,
        ShipTypes.WarpGate, ShipTypes.Beehive, ShipTypes.CarpenterBee, ShipTypes.Factory, ShipTypes.Carrier};
        public static readonly HashSet<ShipTypes> HumanSwarmShips = new HashSet<ShipTypes> { ShipTypes.Scout, ShipTypes.Carrier, ShipTypes.Gunship };
        public static readonly HashSet<ShipTypes> BeePowerfulShips = new HashSet<ShipTypes> { ShipTypes.Queen, ShipTypes.Bumblebee, ShipTypes.Leafcutter };
        public static readonly HashSet<ShipTypes> HumanPowerfulShips = new HashSet<ShipTypes> { ShipTypes.Flagship, ShipTypes.FireBarge, ShipTypes.Cruiser, ShipTypes.Dreadnought };
        public static readonly HashSet<ShipTypes> SpawnedOnlyShipTypes = new HashSet<ShipTypes> { ShipTypes.Drone, ShipTypes.Striker, ShipTypes.Beacon };
        public static readonly HashSet<ShipTypes> ArmedShipTypes = new HashSet<ShipTypes> { ShipTypes.Cruiser, ShipTypes.Dreadnought, ShipTypes.Flagship, ShipTypes.Frigate, ShipTypes.Gunship, ShipTypes.Bumblebee, ShipTypes.Hornet,
            ShipTypes.Leafcutter, ShipTypes.Queen, ShipTypes.Wasp };
        public static readonly List<Data.Map> Maps = new List<Data.Map> { 
            new Data.Map(0, new Vector2(0, -230), new Vector2(0, 230), Locations.Pluto),
            new Data.Map(1, new Vector2(0, -430), new Vector2(0, 430), Locations.Neptune),
            new Data.Map(2, new Vector2(0, -430), new Vector2(0, 430), Locations.Uranus),
            
        };
        public static readonly List<Data.ObstacleMap> ObstacleMaps = new List<Data.ObstacleMap> { 
            new Data.ObstacleMap(0, "None"), 
            new Data.ObstacleMap(1, "Maze") , 
            new Data.ObstacleMap(2, "Three Paths") , 
            new Data.ObstacleMap(0, "Forest"), 
            new Data.ObstacleMap(0, "The Wall") 
        };
        public static int SquadMakerSide;

        public const bool UseWebSocketSharp = true; // Whether to use the "WebSocketSharp" implementation of WebSockets or use the "NativeWebSocket" implmentation
        public static Socket Socket = Test ? new Socket(TestPort, TestServerHostname, UseWebSocketSharp) : new Socket(DevelopmentPort, DevelopmentServerHostname, UseWebSocketSharp);
        public static readonly List<int> InitialVisibleShips = Enumerable.Range(0, 3400).ToList(); // // [alert] [server] Starting ships should be pulled from server
        public static bool FirstTimePlaying = true; // [alert] should be linked to whether a user has actually played before   
        public const float CloseEnoughCoordinateVariance = 1.5f; // world units

        public const int FireBargeExplosionSize = 64;
        public const float RefillDistanceToCarrier = 15;
        public const int MinimumDelayPerBeacon = 10;
        public const int BeaconUpdateFrequency = 5;
        public const int MaxBeaconsDroppedPerScout = 5;
        public const int MinimumClearance = 4;
        public const int MinimumAsteroidSpawnDistance = 100;
        public const int MinimumAsteroidSpeed = 2;
        public const int MinimumAsteroidAngularSpeedMultiplier = 5;
        public const int CollisionAsteroidHealthIncrement = 250;
        public const int MaximumTsvValueForSeeingAShip = 40; // [Tsv]
        public const int MinimumTsvValueForSeeingAShip = 3; // [Tsv]
        public const int StandardReinforcementsDelay = 60;
        public const int StandardMaxCommandTime = 120;
        public const float TsvMultiplierForVision = .1f; // [Tsv]
        public const float VisionShrinkingMultiplier = .8f;
        public static Vector2 HalfSize = new Vector2(.5f, .5f);
        /// <summary>
        /// This is how fast the the ships mine the asteroids. The Mine() method is called every 3 seconds so the the ships gather TSV (and destroy the asteroid) at a
        /// rate of MiningRate / 3 per second [TSV]
        /// </summary>
        public const int MiningRate = 10;
        public static float ShipTurningRadius; 
        public static List<Scenes.Scene> Scenes = new List<Scenes.Scene>();
        public static Scenes.Scene SocketManager;
        public static HashSet<long> UsedHashes = new HashSet<long>();
        /// <summary>
        /// A game wide unique number
        /// </summary>
        public static long UniqueCounter = 0;
        public static WaitForEndOfFrame WaitForEndOfFrame = new WaitForEndOfFrame();
        public static int MaxThreads;
        public static string WaitingMessage = "{\"status\": \"waiting\"}";

        /// <summary>
        /// The obstacle map index that the user has selected, -1 means no selection
        /// </summary>
        //public static int SelectedObstacleMapIndex = -1;
        //public static int SelectedLevelMapIndex = -1;
        //public static int SelectedAsteroidOption = -1;
        //public static int SelecteFogOfWarOption = -1;
        //public static int SelectedMiningOption = -1;
        //public static int SelectedShipsLoadingMidLevelOption = -1;
        //public static int SelectedEnemyShipTypes = -1;
        public static LevelOptions LevelOptions;
        public static bool ChooseRandomLevel;


        //public static KeyCode[] SquadKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };


        public static int ScreenWidth = Screen.width;
        public static int ScreenHeight = Screen.height;
        public const int PixelsPerUnit = 8;
        /// <summary>
        /// How much padding to put on the edges of the map and stop units from moving there.
        /// </summary>
        public static Vector2 MapEdgePadding = new Vector2(5, 5);
        public static Color UnsetColor = Color.clear;

        /// <summary>
        /// The minimum offset between ships in the squad maker in UI world units.
        /// </summary>
        public static Vector2 ShipOffset = new Vector2(15, 15);
        /// <summary>
        /// The distance from the axis before trying to snap the ship into place
        /// </summary>
        public static Vector2 SnapDistance = new Vector2(5, 5);
        /// <summary>
        /// The distance in from the center of the box when placing drag icons in the squad maker, the larger the number the closer to the top of the box
        /// </summary>
        public static int OffsetFromCenterOfSquadMakerDropBox = 230;

        public static Vector2 BaseDragIconSize = new Vector2(.075f, .075f);

        /// <summary>
        /// Supports up to 16 ships, four rows of four columns
        /// </summary>
        public static Vector2[] CarrierDoubleColumnFormationOffsets = new Vector2[] {
            (ShipOffset * new Vector2(-3, 0)),  (ShipOffset * new Vector2(-2, 0)),  (ShipOffset * new Vector2(2, 0)),  (ShipOffset * new Vector2(3, 0)),
            (ShipOffset * new Vector2(-3, -1)), (ShipOffset * new Vector2(-2, -1)), (ShipOffset * new Vector2(2, -1)), (ShipOffset * new Vector2(3, -1)),
            (ShipOffset * new Vector2(-3, -2)), (ShipOffset * new Vector2(-2, -2)), (ShipOffset * new Vector2(2, -2)), (ShipOffset * new Vector2(3, -2)),
            (ShipOffset * new Vector2(-3, -3)), (ShipOffset * new Vector2(-2, -3)), (ShipOffset * new Vector2(2, -3)), (ShipOffset * new Vector2(3, -3)),
            (ShipOffset * new Vector2(-3, -4)), (ShipOffset * new Vector2(-2, -4)), (ShipOffset * new Vector2(2, -4)), (ShipOffset * new Vector2(3, -4)),
        };

        /// <summary>
        /// The amount that each ship in a formation is seperated from other ships, horizontally (Formation Offset X)
        /// </summary>
        public const float FOX = .5f;
        /// <summary>
        /// The amount that each ship in a formation is seperated from other ships, vertically (Formation Offset Y)
        /// </summary>
        public const float FOY = .6f;

        /// <summary>
        /// Supports up to 12 ships, four rows of three columns
        /// </summary>
        public static Vector2[] GeneratedSquadFormationOffsets4x4 = new Vector2[] {
            (ShipOffset * new Vector2(-FOX, 0)), (ShipOffset * new Vector2(0, 0)),  (ShipOffset * new Vector2(FOX, 0)), 
            (ShipOffset * new Vector2(-FOX, -FOY)), (ShipOffset * new Vector2(0, -FOY)), (ShipOffset * new Vector2(FOX, -FOY)),
            (ShipOffset * new Vector2(-FOX, FOY)), (ShipOffset * new Vector2(0, FOY)), (ShipOffset * new Vector2(FOX, FOY)),
            (ShipOffset * new Vector2(-FOX, -FOY * 2)), (ShipOffset * new Vector2(0, -FOY * 2)), (ShipOffset * new Vector2(FOX, -FOY * 2)),
        };

        /// <summary>
        /// Supports up to 12 medium or larger ships, four rows of three columns
        /// </summary>
        public static Vector2[] GeneratedSquadFormationOffsets4x4Medium = new Vector2[] {
            (ShipOffset * new Vector2(-FOX * 1.5f, .75f * FOY)), (ShipOffset * new Vector2(0, .75f * FOY)),  (ShipOffset * new Vector2(FOX * 1.5f, .75f * FOY)),
            (ShipOffset * new Vector2(-FOX * 1.5f, -.75f * FOY)), (ShipOffset * new Vector2(0, -.75f * FOY)), (ShipOffset * new Vector2(FOX * 1.5f, -.75f * FOY)),
            (ShipOffset * new Vector2(-FOX * 1.5f, 2.25f * FOY)), (ShipOffset * new Vector2(0,  2.255f * FOY)), (ShipOffset * new Vector2(FOX * 1.5f,  2.25f * FOY)),
            (ShipOffset * new Vector2(-FOX * 1.5f, -FOY * 2.25f)), (ShipOffset * new Vector2(0, -FOY * 2.25f)), (ShipOffset * new Vector2(FOX * 1.5f, -FOY * 2.25f)),
        };

        /// <summary>
        /// Supports up to 4 SMALL ships, two rows of two columns
        /// </summary>
        public static Vector2[] GeneratedSquadFormationOffsets2x2 = new Vector2[] {
            (ShipOffset * new Vector2(-FOX, .5f * FOY)), (ShipOffset * new Vector2(0, .5f * FOY)),
            (ShipOffset * new Vector2(-FOX, -FOY)), (ShipOffset * new Vector2(0, -FOY)), 
        };

        /// <summary>
        /// Supports up to 4 LARGE ships, two rows of two columns
        /// </summary>
        public static Vector2[] GeneratedSquadFormationOffsets2x2Large = new Vector2[] {
            (ShipOffset * new Vector2(-FOX, FOY)), (ShipOffset * new Vector2(FOX, FOY)),
            (ShipOffset * new Vector2(-FOX, -FOY * 1.5f)), (ShipOffset * new Vector2(FOX, -FOY * 1.5f)),
        };



        public static Vector2[] CarrierColumnFormationOffsets = new Vector2[] {
             (ShipOffset * new Vector2(-FOX, 0)),  (ShipOffset * new Vector2(0, 0)),
             (ShipOffset * new Vector2(-FOX, -FOY)),  (ShipOffset * new Vector2(0, -FOY)),
             //(ShipOffset * new Vector2(-FOX, -2)),  (ShipOffset * new Vector2(FOX, -2)),
             //(ShipOffset * new Vector2(-FOX, -3)),  (ShipOffset * new Vector2(FOX, -3)),
             //(ShipOffset * new Vector2(-FOX, -4)),  (ShipOffset * new Vector2(FOX, -4)),
             //(ShipOffset * new Vector2(-FOX, -5)),  (ShipOffset * new Vector2(FOX, -5)),
             //(ShipOffset * new Vector2(-FOX, -6)),  (ShipOffset * new Vector2(FOX, -6)),
             //(ShipOffset * new Vector2(-FOX, -7)),  (ShipOffset * new Vector2(FOX, -7)),
             //(ShipOffset * new Vector2(-FOX, -8)),  (ShipOffset * new Vector2(FOX, -8)),
             //(ShipOffset * new Vector2(-FOX, -9)),  (ShipOffset * new Vector2(FOX, -9)),
             //(ShipOffset * new Vector2(-FOX, -10)),  (ShipOffset * new Vector2(FOX, -10)),
        };

        public static Vector2[] QueenYellowJacketSpawnFormation = new Vector2[] { // Supports up to 16 slots
            (ShipOffset * new Vector2(-1, 0)), (ShipOffset * new Vector2(-.5f, 0)),  (ShipOffset * new Vector2(.5f, 0)), (ShipOffset * new Vector2(1, 0)),
            (ShipOffset * new Vector2(-1, -1)), (ShipOffset * new Vector2(-.5f, -1)),  (ShipOffset * new Vector2(.5f, -1)), (ShipOffset * new Vector2(1, -1)),
            (ShipOffset * new Vector2(-1, -2)), (ShipOffset * new Vector2(-.5f, -2)),  (ShipOffset * new Vector2(.5f, -2)), (ShipOffset * new Vector2(1, -2)),
            (ShipOffset * new Vector2(-1, -3)), (ShipOffset * new Vector2(-.5f, -3)),  (ShipOffset * new Vector2(.5f, -3)), (ShipOffset * new Vector2(1, -3)),
        };

        public static Vector2 OriginalSavedSquadLabelSize = new Vector2(240, 64);
        public static Color[] FadingAsteroidPiecesColors = new Color[]
        {
            new Color(1, 1, 1, .9f),
            new Color(1, 1, 1, .8f),
            new Color(1, 1, 1, .7f),
            new Color(1, 1, 1, .6f),
            new Color(1, 1, 1, .5f),
            new Color(1, 1, 1, .4f),
            new Color(1, 1, 1, .3f),
            new Color(1, 1, 1, .2f),
            new Color(1, 1, 1, .1f),
        };

        public static Dictionary<string, Color> Colors = new Dictionary<string, Color>() 
        {
            {"good", new Color32(35, 165, 90, 255)},
            {"warning", new Color32(240, 77, 34, 255)},
            {"medium", new Color32(248, 236, 13, 255)},
            {"bad", new Color32(242, 63, 67, 255)},
            {"human", new Color32(39, 127, 94, 255)},
            {"bee", new Color32(251, 242, 54, 255)},
            {"error", new Color32(243, 33, 33, 255)},
            {"squad-ship-counter", new Color32(60, 57, 57, 255)},
            {"supply-capacity-label", new Color32(60, 57, 57, 255)},
            {"invisible", new Color32(255, 255, 255, 0)},
            {"dropbox-background", new Color32(255, 255, 255, 39)},
            {"action-button-normal", new Color32(245, 245, 245, 255)},
            {"action-button-highlight", new Color32(108, 108, 108, 255)},
            {"detonate-button-normal", new Color32(192, 1, 1, 255)},
            {"detonate-button-highlight", new Color32(200, 99, 99, 255)},
            {"eye-aiming", new Color32(242, 63, 67, 255)},
            {"striker-loaded-indicator", new Color32(34, 175, 76, 255)},
            {"striker-not-loaded-indicator", new Color32(236, 44, 44, 255)},
            {"squadbox-default-color", new Color(0.4761926f, 0.8207547f, 0.4979669f, 0.6941177f)},
            {"saved-squad-label-default-color", new Color(0.6527f, 0.6625f, 0.7169f, 1)},
        };


        //Carrying variables - Changing variables that need to be carried between scenes

        /// <summary>
        /// The current set of ships the player is playing with, either the campaign ships, challenge mode ships, or free play ships.
        /// This covers the ships for both sides, the human side and the bee side.
        /// </summary>
        public static Ships CurrentShips = null;
        public static Ships FreePlayShips = null;
        public static Ships CampaignShips = null;
        public static Ships ChallengeModeShips = null;
        //public static List<SavedSquad> SquadsChosenForLevel = new List<SavedSquad>();
        public static bool IsLoadingUserData = false;
        public static bool IsUserLoadingCustomSquads, IsUserLoadingCustomEnemySquads;
        public static GameModes CurrentGameMode = GameModes.FreePlay;

        public static bool AreAllSettingsLoaded => (ShipInfo != null && ShipInfo.IsLoaded) && (Configuration != null && Configuration.IsLoaded)
            && (StartingSettings != null && StartingSettings.IsLoaded);
        public static bool IsAllUserDataLoaded => IsUserProgressDataLoaded && IsFleetDataLoaded[0] && IsFleetDataLoaded[1] && IsSavedSquadsDataLoaded[0] && IsSavedSquadsDataLoaded[1] && IsUserSettingsDataLoaded && IsLevelsDataLoaded[0] && IsLevelsDataLoaded[1];
        public static System.Diagnostics.Stopwatch Stopwatch;
        public static UIAudioController UIAudioController = null;



        // DEBUG VARIABLES
        public static HashSet<ServerRequest> __PastServerRequests = new HashSet<ServerRequest>();
        public static int __TotalResends, __TotalRequests;
        public static double __AverageTimeOnQueue, __TotalLength, __AverageLength, __TotalC2C, __AverageC2C,
            __TotalWireTime, __AverageWireTime, __TotalProcessingTime, __AverageProcessingTime;
        public static long __TotalTimeOnQueue;


        // private variables
        private static int _userId = -1; // [alert] should be set to actual userId and linked to Steam or other account Id
        public static UserProgressData UserProgressData = null;
        private static FleetData _fleetData = null;
        private static SavedSquadsData _savedSquadsData = null;
        private static UserSettingsData _userSettingsData = null;
        private static LevelData _levelData = null;

        private static LevelData _campaignLevelData = null;
        private static FleetData _campaignFleetData = null;
        private static SavedSquadsData _campaignSavedSquadsData = null;


        public static bool HasSocketManager()
        {
            return SocketManager != null;
        }

        /// <summary>
        /// Tries to reconnect to the server
        /// </summary>
        public static void RetryConnection()
        {
            Socket.MakeSocket();
        }
        public static void SwapSides()
        {
            //Debug.Log($"Switching sides from {Configuration.UserSide}");
            if (Configuration.UserSide == Configuration.BeeSide) // if it was the bee side switch to the human side
            {
                //Debug.Log($"Switching sides to humans");
                Configuration.UserSide = Configuration.HumanSide;
                Configuration.AISide = Configuration.BeeSide;
                SquadMakerSide = Configuration.HumanSide;
                Configuration.SquadMakerFirstSide = Configuration.HumanSide;
                Configuration.SquadMakerSecondSide = Configuration.BeeSide;
            }
            else if (Configuration.UserSide == Configuration.HumanSide) // switch to bees
            {
                //Debug.Log($"Switching sides to bees, U: {Configuration.UserSide}, AI: {Configuration.AISide}, H: {Configuration.HumanSide}, B: {Configuration.BeeSide}");
                Configuration.UserSide = Configuration.BeeSide;
                Configuration.AISide = Configuration.HumanSide;
                SquadMakerSide = Configuration.BeeSide;
                Configuration.SquadMakerFirstSide = Configuration.BeeSide;
                Configuration.SquadMakerSecondSide = Configuration.HumanSide;
                //Debug.Log($"Switched sides to bees, U: {Configuration.UserSide}, AI: {Configuration.AISide}, H: {Configuration.HumanSide}, B: {Configuration.BeeSide}");
            }
        }
        public static Color GetUIColor(string name)
        {
            List<string> possibleNames = Colors.Keys.ToList();
            if (possibleNames.Contains(name))
            {
                return Colors.GetValueOrDefault(name);

            }
            else
            {
                Debug.LogError($"Tried to get unknown color name: {name} from list of colors.");
                return Colors.GetValueOrDefault("error");
            }
        }
        public static void SetupUserData()
        {
            if (AreAllSettingsLoaded && !IsAllUserDataLoaded && !IsLoadingUserData)
            {
                IsLoadingUserData = true;
                //Debug.Log("Setting up user data");

                //Debug.Log($"Current Level before loading user data: {GetLevel()}");
                Dictionary<ConfigData.ShipTypes, int> allStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
                StartingSettings.HumanStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));

                Dictionary<ConfigData.ShipTypes, int> allCampaignStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
                StartingSettings.HumanCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));


                SetupUserProgressData(!FirstTimePlaying);
                SetupFleetData(!FirstTimePlaying, allStartingShips);
                SetupCampaignFleetData(!FirstTimePlaying, allCampaignStartingShips);
                SetupSavedSquadsData(!FirstTimePlaying);
                SetupUserSettingsData(!FirstTimePlaying);
                SetupLevelData(!FirstTimePlaying);
                //Debug.Log($"Current Level after loading user data: {GetLevel()}");
            }

        }
        public static void LoadSettings()
        {
            if (!AreAllSettingsLoaded)
            {
                //Debug.Log("Trying to load settings");
                ShipInfo = new ShipStats(GetUserId());
                Configuration = new Configuration(GetUserId());
                StartingSettings = new StartingSettings(GetUserId());
            }

        }
        public static ShipStatBlock GetShipInfo(ShipTypes shipType)
        {
            return ShipInfo.ShipStatsList[shipType];
            //if (ShipInfo != null)
            //{
            //    return ShipInfo.ShipStatsList.GetValueOrDefault(shipType);
            //}
            //else
            //{
            //    Debug.LogError("Tried to get ship info before it was loaded");
            //}
            //return null;
        }

        public static float GetShipSizeFactor(ShipTypes shipType)
        {
            return ShipSizeFactor.GetValueOrDefault(shipType);
        }
        public static void CheckDataFiles()
        {
            if (!IsAllUserDataLoaded)
            {
                //Debug.Log("Checking Data files...");
                //Debug.Log($"Waiting for User Progress Data");
                UserProgressData.WaitForData();
                //Debug.Log($"Waiting for Fleet Data");
                GetFleetData().WaitForData();
                GetCampaignFleetData().WaitForData();
                //Debug.Log($"Waiting for Saved Squads Data");
                GetSavedSquadsData().WaitForData();
                GetCampaignSavedSquadsData().WaitForData();
                //Debug.Log($"Waiting for User Settings Data");
                GetUserSettingsData().WaitForData();
                //Debug.Log($"Waiting for Level Data");
                GetLevelData().WaitForData();
                GetCampaignLevelData().WaitForData();

            }
        }
        public static string GetBasePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{BaseFolder}/";
            string path1 = Application.dataPath + $"/{BaseFolder}";
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
#elif UNITY_ANDROID
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#elif UNITY_IPHONE
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#else
                    string path = Application.dataPath + $"/{BaseFolder}/";
                    string path1 = Application.dataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#endif
        }

        public static string GetCachePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{CacheFolder}/";
            string path1 = Application.dataPath + $"/{CacheFolder}";
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
#elif UNITY_ANDROID
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#elif UNITY_IPHONE
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#else
                    string path = Application.dataPath + $"/{BaseFolder}/";
                    string path1 = Application.dataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#endif
        }
        /// <summary>
        /// Sets up the first time playing data, this is called when the user plays the game for the first time.
        /// </summary>
        public static void SetupFirstTimePlayingHumanCampaign()
        {

            Debug.Log($"Setting up first time playing human campaign data");
            // Do something to show a prompt to the user that they are playing for the first time and need to choose their name [alert]

            // Setup first squad #0

            // Starting Scout Squad
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.HumanCampaignSavedSquadNumber++}", Configuration.HumanSide, ShipTypes.Scout, 1);


            // Starting gunship squad #1
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.HumanCampaignSavedSquadNumber++}", Configuration.HumanSide, ShipTypes.Gunship, 1);
            CurrentShips.GetFleetShip(0).Name = "Gunship D-4";

            // Starting Honeybee squad #2
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Honeybee, 1);

            UserProgressData.HasStartedHumanCampaign = true;

            

            // Starting Hornet squads #3, #4, #5
            for (int j = 0; j < 3; j++) // three hornet squads
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 3);
            }

            // Starting Wasp squads #6, #7
            for (int j = 0; j < 2; j++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 2);
            }


            UserProgressData.HasStartedHumanCampaign = true;

            UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();

        }
        /// <summary>
        /// Prepares the level by either selecting the squads the user will have if they don't get to choose themselves, or by setting up the pre level intro
        /// </summary>
        public static void LoadLevel()
        {
            UserProgressData.GetCurrentLevelOptions(); // sets up the level options for the current level
            LevelOptions = (LevelOptions)UserProgressData.CurrentLevel.Clone(); // Sets the level options for the battle field

            // Makes any level specific adjustments to the ships or intro
            switch (UserProgressData.GetCurrentLevel())
                {
                case 0:
                    LevelOptions.ChosenSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s => s.Id == 0).ToList();
                    SceneManager.LoadSceneAsync("Hivemind Training", LoadSceneMode.Single);
                    Debug.Log($"Loading level 0, setting up pre level intro");
                    break;
                case 1:
                    List<long> level1Squads = new List<long>() { 0, 1, 8, 9 };
                    LevelOptions.ChosenSquads = CurrentShips.GetSavedSquads().Where(s => level1Squads.Contains(s.Id)).ToList();
                    Debug.Log($"Loading level 1, setting up pre level intro");
                    break;
                default:
                    Debug.LogError($"Tried to load unknown level {UserProgressData.GetCurrentLevel()}");
                    break;
            }

            // Go to the intro scene if there is one, otherwise go straight to the battle scene
        }
        public static int GetUserId()
        {
            if (_userId == -1 && !PlayerPrefs.HasKey("userId"))
            {
                _userId = Utilities.RandomInt();
                PlayerPrefs.SetInt("userId", _userId);
            }
            else
            {
                _userId = PlayerPrefs.GetInt("userId");
                FirstTimePlaying = false;
            }
            //Debug.Log($"User id is {_userId}");
            return _userId;
        }
        public static void SetupUserProgressData(bool shouldFileExist)
        {
            UserProgressData = new UserProgressData(shouldFileExist);
        }
        public static void SetupUserSettingsData(bool shouldFileExist)
        {
            _userSettingsData = new UserSettingsData(shouldFileExist);
        }
        public static UserSettingsData GetUserSettingsData()
        {
            return _userSettingsData;
        }
        public static void SetupLevelData(bool shouldFileExist)
        {
            _campaignLevelData = new LevelData(shouldFileExist, 0);
            _levelData = new LevelData(shouldFileExist, 1);
        }
        public static LevelData GetLevelData()
        {
            return _levelData;
        }
        public static LevelData GetCampaignLevelData()
        {
            return _campaignLevelData;
        }
        public static void SetupCampaignFleetData(bool shouldFileExist, Dictionary<ShipTypes, int> startingShips)
        {
            _campaignFleetData = new FleetData(shouldFileExist, startingShips, 0);
        }
        public static void SetupFleetData(bool shouldFileExist, Dictionary<ShipTypes, int> startingShips)
        {
            _fleetData = new FleetData(shouldFileExist, startingShips, 1);
        }
        public static FleetData GetFleetData()
        {
            return _fleetData;
        }
        public static FleetData GetCampaignFleetData()
        {
            return _campaignFleetData;
        }
        public static void SetupSavedSquadsData(bool shouldFileExist)
        {
            _campaignSavedSquadsData = new SavedSquadsData(shouldFileExist, 0);
            _savedSquadsData = new SavedSquadsData(shouldFileExist, 1);
        }
        public static SavedSquadsData GetSavedSquadsData()
        {
            return _savedSquadsData;
        }
        public static SavedSquadsData GetCampaignSavedSquadsData()
        {
            return _campaignSavedSquadsData;
        }

    }
}