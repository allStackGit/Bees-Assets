using Assets.Scripts.Data;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        public const int ObstaclesLayerMask = 1 << 19;
        public const int ObstacleProximityRangesLayerMask = 1 << 20;
        public const int BeeShipsLayerMask = 1 << 11;
        public const int FogOfWarLayer = 21;
        public const int VisionRangesLayer = 22;

        public const int Tiny = 1;
        public const float Small = 1.5f;
        public const int Medium = 2;
        public const int Large = 3;
        public const int Huge = 4;
        public const int Enormous = 8;
        public const int Unfathomable = 32;

        public static readonly Dictionary<ShipTypes, Vector2Int> ShipSizes = new Dictionary<ShipTypes, Vector2Int>
        {
            { ShipTypes.Barge, new Vector2Int(152, 72) },
            { ShipTypes.Beacon, new Vector2Int(18, 16) },
            { ShipTypes.Carrier, new Vector2Int(96, 112) },
            { ShipTypes.Cruiser, new Vector2Int(64, 72) },
            { ShipTypes.Dreadnought, new Vector2Int(64, 84) },
            { ShipTypes.Drone, new Vector2Int(32, 32) },
            { ShipTypes.Factory, new Vector2Int(128, 128) },
            { ShipTypes.FireBarge, new Vector2Int(152, 72) },
            { ShipTypes.Flagship, new Vector2Int(128, 152) },
            { ShipTypes.Frigate, new Vector2Int(48, 48) },
            { ShipTypes.Gunship, new Vector2Int(48, 48) },
            { ShipTypes.Scout, new Vector2Int(40, 32) },
            { ShipTypes.Striker, new Vector2Int(32, 32) },
            { ShipTypes.WarpGate, new Vector2Int(224, 128) },
            { ShipTypes.HumanTarget, new Vector2Int(256, 256) },
            { ShipTypes.Beehive, new Vector2Int(272, 272) },
            { ShipTypes.Bumblebee, new Vector2Int(136, 96) },
            { ShipTypes.CarpenterBee, new Vector2Int(128, 128) },
            { ShipTypes.Honeybee, new Vector2Int(32, 32) },
            { ShipTypes.Hornet, new Vector2Int(32, 32) },
            { ShipTypes.Leafcutter, new Vector2Int(64, 64) },
            { ShipTypes.Queen, new Vector2Int(1280, 1024) },
            { ShipTypes.Wasp, new Vector2Int(48, 48) },
            { ShipTypes.YellowJacket, new Vector2Int(32, 32) },
        };

        public static readonly Dictionary<ShipTypes, Vector2Int> ShipRemainsSizes = new Dictionary<ShipTypes, Vector2Int>
        {
            { ShipTypes.Barge, new Vector2Int(254, 254) },
            { ShipTypes.Beacon, new Vector2Int(0, 0) },
            { ShipTypes.Carrier, new Vector2Int(198, 198) },
            { ShipTypes.Cruiser, new Vector2Int(158, 158) },
            { ShipTypes.Dreadnought, new Vector2Int(134, 134) },
            { ShipTypes.Drone, new Vector2Int(86, 86) },
            { ShipTypes.Factory, new Vector2Int(238, 238) },
            { ShipTypes.FireBarge, new Vector2Int(0, 0) },
            { ShipTypes.Flagship, new Vector2Int(210, 238) },
            { ShipTypes.Frigate, new Vector2Int(100, 100) },
            { ShipTypes.Gunship, new Vector2Int(100, 100) },
            { ShipTypes.Scout, new Vector2Int(80, 80) },
            { ShipTypes.Striker, new Vector2Int(78, 78) },
            { ShipTypes.WarpGate, new Vector2Int(382, 238) },
            { ShipTypes.Beehive, new Vector2Int(0, 0) },
            { ShipTypes.Bumblebee, new Vector2Int(0, 0) },
            { ShipTypes.CarpenterBee, new Vector2Int(0, 0) },
            { ShipTypes.Honeybee, new Vector2Int(0, 0) },
            { ShipTypes.Hornet, new Vector2Int(0, 0) },
            { ShipTypes.Leafcutter, new Vector2Int(0, 0) },
            { ShipTypes.Queen, new Vector2Int(0, 0) },
            { ShipTypes.Wasp, new Vector2Int(0, 0) },
            { ShipTypes.YellowJacket, new Vector2Int(0, 0) },
        };

        public static readonly Dictionary<ShipTypes, float> ShipSizeFactor = new Dictionary<ShipTypes, float>
        {
            { ShipTypes.Barge, Huge },
            { ShipTypes.Beacon, Tiny },
            { ShipTypes.Carrier, Large },
            { ShipTypes.Cruiser, Medium },
            { ShipTypes.Dreadnought, Medium },
            { ShipTypes.Drone, Tiny },
            { ShipTypes.Factory, Huge },
            { ShipTypes.FireBarge, Huge },
            { ShipTypes.Flagship, Huge },
            { ShipTypes.Frigate, Small },
            { ShipTypes.Gunship, Small },
            { ShipTypes.Scout, Tiny },
            { ShipTypes.Striker, Tiny },
            { ShipTypes.WarpGate, Huge },
            { ShipTypes.Beehive, Enormous },
            { ShipTypes.Bumblebee, Large },
            { ShipTypes.CarpenterBee, Huge },
            { ShipTypes.Honeybee, Tiny },
            { ShipTypes.Hornet, Tiny },
            { ShipTypes.Leafcutter, Medium },
            { ShipTypes.Queen, Unfathomable },
            { ShipTypes.Wasp, Small },
            { ShipTypes.YellowJacket, Tiny },
        };

        public static readonly Dictionary<ShipTypes, Color[]> ChangeableShipColors = new Dictionary<ShipTypes, Color[]>
        {
            { ShipTypes.Barge, new[] { new Color(0.235f, 0.753f, 0.498f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.1098f, 0.3568f, 0.2352f, 1) } },
            { ShipTypes.Beacon, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { ShipTypes.Carrier, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1098f, 0.3568f, 0.2352f, 1) } },
            { ShipTypes.Cruiser, new[] { new Color(0.184f, 0.569f, 0.380f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.1098f, 0.3568f, 0.2352f, 1) } },
            { ShipTypes.Dreadnought, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1176f, 0.3725f, 0.2470f, 1) } },
            { ShipTypes.Drone, new[] { new Color(.729f, .729f, .729f, 1) } },
            { ShipTypes.Factory, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1176f, 0.3725f, 0.2470f, 1) } },
            { ShipTypes.FireBarge, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.235f, 0.753f, 0.498f, 1) } },
            { ShipTypes.Flagship, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1098f, 0.3568f, 0.2352f, 1) } },
            { ShipTypes.Frigate, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1058f, 0.3607f, 0.2352f, 1), new Color(0.1607f, 0.5411f, 0.3529f, 1), new Color(0.0941f, 0.3019f, 0.2235f, 1), new Color(0.1607f, 0.4431f, 0.2901f, 1), new Color(0.1294f, 0.3176f, 0.2235f, 1) } },
            { ShipTypes.Gunship, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1843f, 0.5686f, 0.3725f, 1), new Color(0.1607f, 0.4823f, 0.3215f, 1), new Color(0.1921f, 0.6039f, 0.3960f, 1), new Color(0.1607f, 0.5098f, 0.3215f, 1), new Color(0.1450f, 0.4588f, 0.2941f, 1), new Color(0.1921f, 0.6039f, 0.3882f, 1), new Color(0.1921f, 0.5882f, 0.3882f, 1), new Color(0.1921f, 0.5568f, 0.3882f, 1), new Color(0.1921f, 0.5882f, 0.4196f, 1), new Color(0.2f, 0.5607f, 0.3803f, 1), new Color(0.1647f, 0.5254f, 0.3450f, 1) } },
            { ShipTypes.Scout, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1843f, 0.5686f, 0.3725f, 1), new Color(0.1607f, 0.4823f, 0.3215f, 1), new Color(0.1921f, 0.6039f, 0.3960f, 1), new Color(0.1607f, 0.5098f, 0.3215f, 1), new Color(0.1450f, 0.4588f, 0.2941f, 1), new Color(0.1921f, 0.6039f, 0.3882f, 1), new Color(0.1921f, 0.5882f, 0.3882f, 1), new Color(0.1921f, 0.5568f, 0.3882f, 1), new Color(0.1921f, 0.5882f, 0.4196f, 1) } },
            { ShipTypes.Striker, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { ShipTypes.WarpGate, new[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1098f, 0.3568f, 0.2352f, 1) } },
            { ShipTypes.Beehive, new[] { UnsetColor } },
            { ShipTypes.Bumblebee, new[] { UnsetColor } },
            { ShipTypes.CarpenterBee, new[] { UnsetColor } },
            { ShipTypes.Honeybee, new[] { UnsetColor } },
            { ShipTypes.Hornet, new[] { UnsetColor } },
            { ShipTypes.Leafcutter, new[] { UnsetColor } },
            { ShipTypes.Queen, new[] { UnsetColor } },
            { ShipTypes.Wasp, new[] { UnsetColor } },
            { ShipTypes.YellowJacket, new[] { UnsetColor } },
        };

        public static readonly Dictionary<ShipTypes, float> OffsetFromFrontOfShip = new Dictionary<ShipTypes, float>
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
            { ShipTypes.CarpenterBee, 3.55f },
            { ShipTypes.Honeybee, .35f },
            { ShipTypes.Hornet, .35f },
            { ShipTypes.Leafcutter, .35f },
            { ShipTypes.Queen, .35f },
            { ShipTypes.Wasp, 1f },
            { ShipTypes.YellowJacket, .55f },
        };

        public static readonly HashSet<CommandTypes> TypesOfCommands = new HashSet<CommandTypes>
        {
            CommandTypes.Aggressive,
            CommandTypes.BombingRun,
            CommandTypes.Charge,
            CommandTypes.Retreat,
            CommandTypes.MoveToRandom,
            CommandTypes.CircleSquad,
            CommandTypes.RightSwipe,
            CommandTypes.LeftSwipe,
            CommandTypes.ClosestFriendly,
            CommandTypes.InAndOut,
            CommandTypes.Patrol,
            CommandTypes.Guard,
            CommandTypes.Scouting,
            CommandTypes.Mining,
            CommandTypes.FullRetreat,
            CommandTypes.Hold,
            CommandTypes.Heal
        };

        public static readonly List<ShootingStrategyTypes> TypesOfShootingStrategies = new List<ShootingStrategyTypes>
        {
            ShootingStrategyTypes.FirstSeen,
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
            ShootingStrategyTypes.LeastValuable,
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
            ShootingStrategyTypes.TypeX,
        };

        public static ShootingStrategyTypes DefaultShootingStrategy = ShootingStrategyTypes.FirstSeen;

        public static HashSet<string> ShootingStrategyNames = new HashSet<string>
        {
            "First Seen", "Random", "Revenge", "Most Dangerous", "Most Health", "Least Health", "Most Powerful", "Least Powerful", "Closest", "Furthest",
            "Most Range", "Least Range", "Fastest", "Slowest", "Most Valuable", "Least Valuable", "Type A", "Type B", "Type C", "Type D", "Type E", "Type F", "Type G", "Type H", "Type I", "Type J", "Type K", "Type L",
            "Type M", "Type N", "Type O", "Type P", "Type Q", "Type R", "Type S", "Type T", "Type U", "Type V, Type W, Type X"
        };

        public static HashSet<ShipTypes> BeeShipTypes = new HashSet<ShipTypes>();
        public static HashSet<ShipTypes> HumanShipTypes = new HashSet<ShipTypes>();
        public static readonly HashSet<ShipTypes> BeeSwarmShips = new HashSet<ShipTypes> { ShipTypes.Honeybee, ShipTypes.Hornet, ShipTypes.YellowJacket };
        public static readonly HashSet<ShipTypes> SmallShips = new HashSet<ShipTypes> { ShipTypes.Honeybee, ShipTypes.Hornet, ShipTypes.YellowJacket, ShipTypes.Scout, ShipTypes.Gunship, ShipTypes.Drone, ShipTypes.Wasp, ShipTypes.Striker, ShipTypes.Beacon, ShipTypes.Frigate };
        public static readonly HashSet<ShipTypes> MediumShips = new HashSet<ShipTypes> { ShipTypes.Cruiser, ShipTypes.Dreadnought, ShipTypes.Leafcutter };
        public static readonly HashSet<ShipTypes> LargeShips = new HashSet<ShipTypes> { ShipTypes.Queen, ShipTypes.Flagship, ShipTypes.Barge, ShipTypes.FireBarge, ShipTypes.Bumblebee, ShipTypes.WarpGate, ShipTypes.Beehive, ShipTypes.CarpenterBee, ShipTypes.Factory, ShipTypes.Carrier };
        public static readonly HashSet<ShipTypes> HumanSwarmShips = new HashSet<ShipTypes> { ShipTypes.Scout, ShipTypes.Carrier, ShipTypes.Gunship };
        public static readonly HashSet<ShipTypes> BeePowerfulShips = new HashSet<ShipTypes> { ShipTypes.Queen, ShipTypes.Bumblebee, ShipTypes.Leafcutter };
        public static readonly HashSet<ShipTypes> HumanPowerfulShips = new HashSet<ShipTypes> { ShipTypes.Flagship, ShipTypes.FireBarge, ShipTypes.Cruiser, ShipTypes.Dreadnought };
        public static readonly HashSet<ShipTypes> SpawnedOnlyShipTypes = new HashSet<ShipTypes> { ShipTypes.Drone, ShipTypes.Striker, ShipTypes.Beacon };
        public static readonly HashSet<ShipTypes> ArmedShipTypes = new HashSet<ShipTypes> { ShipTypes.Cruiser, ShipTypes.Dreadnought, ShipTypes.Flagship, ShipTypes.Frigate, ShipTypes.Gunship, ShipTypes.Bumblebee, ShipTypes.Hornet, ShipTypes.Leafcutter, ShipTypes.Queen, ShipTypes.Wasp };

        public static readonly List<Data.Map> Maps = new List<Data.Map>
        {
            new Data.Map(0, new Vector2(0, -230), new Vector2(0, 230), Locations.Pluto),
            new Data.Map(1, new Vector2(0, -430), new Vector2(0, 430), Locations.Neptune),
            new Data.Map(2, new Vector2(0, -215), new Vector2(0, 215), Locations.Titania),
            new Data.Map(3, new Vector2(0, -430), new Vector2(0, 430), Locations.Uranus),
        };

        public static readonly List<Data.ObstacleMap> ObstacleMaps = new List<Data.ObstacleMap>
        {
            new Data.ObstacleMap(0, "None"),
            new Data.ObstacleMap(1, "Maze"),
            new Data.ObstacleMap(2, "Three Paths"),
            new Data.ObstacleMap(0, "Forest"),
            new Data.ObstacleMap(0, "The Wall")
        };

        public const float CloseEnoughCoordinateVariance = 1.5f;
        public const int FireBargeExplosionSize = 64;
        public const float RefillDistanceToCarrier = 15;
        public const int MinimumDelayPerBeacon = 10;
        public const int BeaconUpdateFrequency = 5;
        public const int MaxBeaconsDroppedPerScout = 5;
        public const int MinimumClearance = 2;
        public const int MinimumAsteroidSpawnDistance = 100;
        public const int MinimumAsteroidSpeed = 2;
        public const int MinimumAsteroidAngularSpeedMultiplier = 5;
        public const int CollisionAsteroidHealthIncrement = 250;
        public const int MaximumTsvValueForSeeingAShip = 40;
        public const int MinimumTsvValueForSeeingAShip = 3;
        public const int StandardReinforcementsDelay = 60;
        public const int StandardMaxCommandTime = 120;
        public const float TsvMultiplierForVision = .1f;
        public const float VisionShrinkingMultiplier = .8f;
        public const int MiningRate = 5;
        public const int PixelsPerUnit = 8;

        public static Vector2 HalfSize = new Vector2(.5f, .5f);
        public static Vector2 StartingPositionOffset = new Vector2(-33, -2);
        public static Vector2 MapEdgePadding = new Vector2(5, 5);
        public static Color UnsetColor = Color.clear;
        public static Vector2 ShipOffset = new Vector2(15, 15);
        public static Vector2 SnapDistance = new Vector2(5, 5);
        public static int OffsetFromCenterOfSquadMakerDropBox = 230;
        public static Vector2 BaseDragIconSize = new Vector2(.075f, .075f);

        public static Vector2[] CarrierDoubleColumnFormationOffsets =
        {
            ShipOffset * new Vector2(-3, 0), ShipOffset * new Vector2(-2, 0), ShipOffset * new Vector2(2, 0), ShipOffset * new Vector2(3, 0),
            ShipOffset * new Vector2(-3, -1), ShipOffset * new Vector2(-2, -1), ShipOffset * new Vector2(2, -1), ShipOffset * new Vector2(3, -1),
            ShipOffset * new Vector2(-3, -2), ShipOffset * new Vector2(-2, -2), ShipOffset * new Vector2(2, -2), ShipOffset * new Vector2(3, -2),
            ShipOffset * new Vector2(-3, -3), ShipOffset * new Vector2(-2, -3), ShipOffset * new Vector2(2, -3), ShipOffset * new Vector2(3, -3),
            ShipOffset * new Vector2(-3, -4), ShipOffset * new Vector2(-2, -4), ShipOffset * new Vector2(2, -4), ShipOffset * new Vector2(3, -4),
        };

        public const float FOX = .5f;
        public const float FOY = .6f;

        public static Vector2[] GeneratedSquadFormationOffsets4x4 =
        {
            ShipOffset * new Vector2(-FOX, 0), ShipOffset * new Vector2(0, 0), ShipOffset * new Vector2(FOX, 0),
            ShipOffset * new Vector2(-FOX, -FOY), ShipOffset * new Vector2(0, -FOY), ShipOffset * new Vector2(FOX, -FOY),
            ShipOffset * new Vector2(-FOX, FOY), ShipOffset * new Vector2(0, FOY), ShipOffset * new Vector2(FOX, FOY),
            ShipOffset * new Vector2(-FOX, -FOY * 2), ShipOffset * new Vector2(0, -FOY * 2), ShipOffset * new Vector2(FOX, -FOY * 2),
        };

        public static Vector2[] GeneratedSquadFormationOffsets4x4Medium =
        {
            ShipOffset * new Vector2(-FOX * 1.5f, .75f * FOY), ShipOffset * new Vector2(0, .75f * FOY), ShipOffset * new Vector2(FOX * 1.5f, .75f * FOY),
            ShipOffset * new Vector2(-FOX * 1.5f, -.75f * FOY), ShipOffset * new Vector2(0, -.75f * FOY), ShipOffset * new Vector2(FOX * 1.5f, -.75f * FOY),
            ShipOffset * new Vector2(-FOX * 1.5f, 2.25f * FOY), ShipOffset * new Vector2(0, 2.255f * FOY), ShipOffset * new Vector2(FOX * 1.5f, 2.25f * FOY),
            ShipOffset * new Vector2(-FOX * 1.5f, -FOY * 2.25f), ShipOffset * new Vector2(0, -FOY * 2.25f), ShipOffset * new Vector2(FOX * 1.5f, -FOY * 2.25f),
        };

        public static Vector2[] GeneratedSquadFormationOffsets2x2 =
        {
            ShipOffset * new Vector2(-FOX * .5f, .5f * FOY), ShipOffset * new Vector2(FOX * .5f, .5f * FOY),
            ShipOffset * new Vector2(-FOX * .5f, -FOY), ShipOffset * new Vector2(FOX * .5f, -FOY),
        };

        public static Vector2[] GeneratedSquadFormationOffsets2x2Large =
        {
            ShipOffset * new Vector2(-FOX, FOY), ShipOffset * new Vector2(FOX, FOY),
            ShipOffset * new Vector2(-FOX, -FOY * 1.5f), ShipOffset * new Vector2(FOX, -FOY * 1.5f),
        };

        public static Vector2[] CarrierColumnFormationOffsets =
        {
            ShipOffset * new Vector2(-FOX, 0), ShipOffset * new Vector2(0, 0),
            ShipOffset * new Vector2(-FOX, -FOY), ShipOffset * new Vector2(0, -FOY),
        };

        public static Vector2[] QueenYellowJacketSpawnFormation =
        {
            ShipOffset * new Vector2(-1, 0), ShipOffset * new Vector2(-.5f, 0), ShipOffset * new Vector2(.5f, 0), ShipOffset * new Vector2(1, 0),
            ShipOffset * new Vector2(-1, -1), ShipOffset * new Vector2(-.5f, -1), ShipOffset * new Vector2(.5f, -1), ShipOffset * new Vector2(1, -1),
            ShipOffset * new Vector2(-1, -2), ShipOffset * new Vector2(-.5f, -2), ShipOffset * new Vector2(.5f, -2), ShipOffset * new Vector2(1, -2),
            ShipOffset * new Vector2(-1, -3), ShipOffset * new Vector2(-.5f, -3), ShipOffset * new Vector2(.5f, -3), ShipOffset * new Vector2(1, -3),
        };

        public static Vector2 OriginalSavedSquadLabelSize = new Vector2(240, 64);

        public static Color[] FadingAsteroidPiecesColors =
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

        public static Dictionary<string, Color> Colors = new Dictionary<string, Color>
        {
            { "good", new Color32(35, 165, 90, 255) },
            { "warning", new Color32(240, 77, 34, 255) },
            { "medium", new Color32(248, 236, 13, 255) },
            { "bad", new Color32(242, 63, 67, 255) },
            { "human", new Color32(39, 127, 94, 255) },
            { "bee", new Color32(251, 242, 54, 255) },
            { "error", new Color32(243, 33, 33, 255) },
            { "squad-ship-counter", new Color32(60, 57, 57, 255) },
            { "supply-capacity-label", new Color32(60, 57, 57, 255) },
            { "invisible", new Color32(255, 255, 255, 0) },
            { "dropbox-background", new Color32(255, 255, 255, 39) },
            { "action-button-normal", new Color32(245, 245, 245, 255) },
            { "action-button-highlight", new Color32(108, 108, 108, 255) },
            { "detonate-button-normal", new Color32(192, 1, 1, 255) },
            { "detonate-button-highlight", new Color32(200, 99, 99, 255) },
            { "eye-aiming", new Color32(242, 63, 67, 255) },
            { "striker-loaded-indicator", new Color32(34, 175, 76, 255) },
            { "striker-not-loaded-indicator", new Color32(236, 44, 44, 255) },
            { "squadbox-default-color", new Color(0.4761926f, 0.8207547f, 0.4979669f, 0.6941177f) },
            { "saved-squad-label-default-color", new Color(0.6527f, 0.6625f, 0.7169f, 1) },
            { "ui-green-screen", new Color(0.1803f, 0.8078f, 0.5568f, 1) },
        };
    }
}
