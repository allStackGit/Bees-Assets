using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Unity.Mathematics;

using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using Random = System.Random;

namespace Assets.Scripts
{
    
    public static class Utilities
    {
        public static readonly Dictionary<ConfigData.ShipTypeLetters, ConfigData.ShipTypes> ConvertShipTypeLetterToShipType = new Dictionary<ConfigData.ShipTypeLetters, ConfigData.ShipTypes>()
        {
            {ConfigData.ShipTypeLetters.A, ConfigData.ShipTypes.Queen },
            {ConfigData.ShipTypeLetters.B, ConfigData.ShipTypes.Hornet },
            {ConfigData.ShipTypeLetters.C, ConfigData.ShipTypes.Dreadnought },
            {ConfigData.ShipTypeLetters.D, ConfigData.ShipTypes.Gunship },
            {ConfigData.ShipTypeLetters.E, ConfigData.ShipTypes.Scout },
            {ConfigData.ShipTypeLetters.F, ConfigData.ShipTypes.Wasp },
            {ConfigData.ShipTypeLetters.G, ConfigData.ShipTypes.Bumblebee },
            {ConfigData.ShipTypeLetters.H, ConfigData.ShipTypes.Flagship },
            {ConfigData.ShipTypeLetters.I, ConfigData.ShipTypes.Honeybee },
            {ConfigData.ShipTypeLetters.J, ConfigData.ShipTypes.CarpenterBee },
            {ConfigData.ShipTypeLetters.K, ConfigData.ShipTypes.Leafcutter },
            {ConfigData.ShipTypeLetters.L, ConfigData.ShipTypes.YellowJacket },
            {ConfigData.ShipTypeLetters.M, ConfigData.ShipTypes.Beehive },
            {ConfigData.ShipTypeLetters.N, ConfigData.ShipTypes.Frigate },
            {ConfigData.ShipTypeLetters.O, ConfigData.ShipTypes.Carrier },
            {ConfigData.ShipTypeLetters.P, ConfigData.ShipTypes.Drone },
            {ConfigData.ShipTypeLetters.Q, ConfigData.ShipTypes.Striker },
            {ConfigData.ShipTypeLetters.R, ConfigData.ShipTypes.Factory },
            {ConfigData.ShipTypeLetters.S, ConfigData.ShipTypes.Cruiser },
            {ConfigData.ShipTypeLetters.T, ConfigData.ShipTypes.Barge },
            {ConfigData.ShipTypeLetters.U, ConfigData.ShipTypes.FireBarge },
            {ConfigData.ShipTypeLetters.V, ConfigData.ShipTypes.WarpGate },
            {ConfigData.ShipTypeLetters.W, ConfigData.ShipTypes.Beacon },
        };
        public static readonly Dictionary<ConfigData.ShipTypeLetters, char> ConvertShipTypeLetterToCharacter = new Dictionary<ConfigData.ShipTypeLetters, char>()
        {
            {ConfigData.ShipTypeLetters.A, 'A' },
            {ConfigData.ShipTypeLetters.B, 'B' },
            {ConfigData.ShipTypeLetters.C, 'C' },
            {ConfigData.ShipTypeLetters.D, 'D' },
            {ConfigData.ShipTypeLetters.E, 'E' },
            {ConfigData.ShipTypeLetters.F, 'F' },
            {ConfigData.ShipTypeLetters.G, 'G' },
            {ConfigData.ShipTypeLetters.H, 'H' },
            {ConfigData.ShipTypeLetters.I, 'I' },
            {ConfigData.ShipTypeLetters.J, 'J' },
            {ConfigData.ShipTypeLetters.K, 'K' },
            {ConfigData.ShipTypeLetters.L, 'L' },
            {ConfigData.ShipTypeLetters.M, 'M' },
            {ConfigData.ShipTypeLetters.N, 'N' },
            {ConfigData.ShipTypeLetters.O, 'O' },
            {ConfigData.ShipTypeLetters.P, 'P' },
            {ConfigData.ShipTypeLetters.Q, 'Q' },
            {ConfigData.ShipTypeLetters.R, 'R' },
            {ConfigData.ShipTypeLetters.S, 'S' },
            {ConfigData.ShipTypeLetters.T, 'T' },
            {ConfigData.ShipTypeLetters.U, 'U' },
            {ConfigData.ShipTypeLetters.V, 'V' },
            {ConfigData.ShipTypeLetters.W, 'W' },
        };
        public static readonly Dictionary<ConfigData.ShipTypes, char> ConvertShipTypeToCharacter = new Dictionary<ConfigData.ShipTypes, char>()
        {
            {ConfigData.ShipTypes.Queen, 'A' },
            {ConfigData.ShipTypes.Hornet, 'B' },
            {ConfigData.ShipTypes.Dreadnought, 'C' },
            {ConfigData.ShipTypes.Gunship, 'D' },
            {ConfigData.ShipTypes.Scout, 'E' },
            {ConfigData.ShipTypes.Wasp, 'F' },
            {ConfigData.ShipTypes.Bumblebee, 'G' },
            {ConfigData.ShipTypes.Flagship, 'H' },
            {ConfigData.ShipTypes.Honeybee, 'I' },
            {ConfigData.ShipTypes.CarpenterBee, 'J' },
            {ConfigData.ShipTypes.Leafcutter, 'K' },
            {ConfigData.ShipTypes.YellowJacket, 'L' },
            {ConfigData.ShipTypes.Beehive, 'M' },
            {ConfigData.ShipTypes.Frigate, 'N' },
            {ConfigData.ShipTypes.Carrier, 'O' },
            {ConfigData.ShipTypes.Drone, 'P' },
            {ConfigData.ShipTypes.Striker, 'Q' },
            {ConfigData.ShipTypes.Factory, 'R' },
            {ConfigData.ShipTypes.Cruiser, 'S' },
            {ConfigData.ShipTypes.Barge, 'T' },
            {ConfigData.ShipTypes.FireBarge, 'U' },
            {ConfigData.ShipTypes.WarpGate, 'V' },
            {ConfigData.ShipTypes.Beacon, 'W' },
        };
        public static readonly Dictionary<ConfigData.ShipTypes, ConfigData.ShipTypeLetters> ConvertShipTypeToShipTypeLetter = new Dictionary<ConfigData.ShipTypes, ConfigData.ShipTypeLetters>()
        {
            {ConfigData.ShipTypes.Queen, ConfigData.ShipTypeLetters.A },
            {ConfigData.ShipTypes.Hornet, ConfigData.ShipTypeLetters.B },
            {ConfigData.ShipTypes.Dreadnought, ConfigData.ShipTypeLetters.C },
            {ConfigData.ShipTypes.Gunship, ConfigData.ShipTypeLetters.D },
            {ConfigData.ShipTypes.Scout, ConfigData.ShipTypeLetters.E },
            {ConfigData.ShipTypes.Wasp, ConfigData.ShipTypeLetters.F },
            {ConfigData.ShipTypes.Bumblebee, ConfigData.ShipTypeLetters.G },
            {ConfigData.ShipTypes.Flagship, ConfigData.ShipTypeLetters.H },
            {ConfigData.ShipTypes.Honeybee, ConfigData.ShipTypeLetters.I },
            {ConfigData.ShipTypes.CarpenterBee, ConfigData.ShipTypeLetters.J },
            {ConfigData.ShipTypes.Leafcutter, ConfigData.ShipTypeLetters.K },
            {ConfigData.ShipTypes.YellowJacket, ConfigData.ShipTypeLetters.L },
            {ConfigData.ShipTypes.Beehive, ConfigData.ShipTypeLetters.M },
            {ConfigData.ShipTypes.Frigate, ConfigData.ShipTypeLetters.N },
            {ConfigData.ShipTypes.Carrier, ConfigData.ShipTypeLetters.O },
            {ConfigData.ShipTypes.Drone, ConfigData.ShipTypeLetters.P },
            {ConfigData.ShipTypes.Striker, ConfigData.ShipTypeLetters.Q },
            {ConfigData.ShipTypes.Factory, ConfigData.ShipTypeLetters.R },
            {ConfigData.ShipTypes.Cruiser, ConfigData.ShipTypeLetters.S },
            {ConfigData.ShipTypes.Barge, ConfigData.ShipTypeLetters.T },
            {ConfigData.ShipTypes.FireBarge, ConfigData.ShipTypeLetters.U },
            {ConfigData.ShipTypes.WarpGate, ConfigData.ShipTypeLetters.V },
            {ConfigData.ShipTypes.Beacon, ConfigData.ShipTypeLetters.W },
        };

        public static readonly Dictionary<ConfigData.ShipTypes, int> ConvertShipTypeToSide = new Dictionary<ConfigData.ShipTypes, int>()
        {
            {ConfigData.ShipTypes.Queen, 1 },
            {ConfigData.ShipTypes.Hornet, 1 },
            {ConfigData.ShipTypes.Dreadnought, 2 },
            {ConfigData.ShipTypes.Gunship, 2 },
            {ConfigData.ShipTypes.Scout, 2 },
            {ConfigData.ShipTypes.Wasp, 1 },
            {ConfigData.ShipTypes.Bumblebee, 1 },
            {ConfigData.ShipTypes.Flagship, 2 },
            {ConfigData.ShipTypes.Honeybee, 1 },
            {ConfigData.ShipTypes.CarpenterBee, 1 },
            {ConfigData.ShipTypes.Leafcutter, 1 },
            {ConfigData.ShipTypes.YellowJacket, 1 },
            {ConfigData.ShipTypes.Beehive, 1 },
            {ConfigData.ShipTypes.Frigate, 2 },
            {ConfigData.ShipTypes.Carrier, 2 },
            {ConfigData.ShipTypes.Drone, 2 },
            {ConfigData.ShipTypes.Striker, 2 },
            {ConfigData.ShipTypes.Factory, 2 },
            {ConfigData.ShipTypes.Cruiser, 2 },
            {ConfigData.ShipTypes.Barge, 2 },
            {ConfigData.ShipTypes.FireBarge, 2 },
            {ConfigData.ShipTypes.WarpGate, 2 },
            {ConfigData.ShipTypes.Beacon, 2 },
        };

        public static readonly Dictionary<ConfigData.ProjectileTypes, int> ConvertProjectileTypeToSide = new Dictionary<ConfigData.ProjectileTypes, int>()
        {
            {ConfigData.ProjectileTypes.BeeSmall, 1 },
            {ConfigData.ProjectileTypes.BeeMedium, 1 },
            {ConfigData.ProjectileTypes.BumblebeeShot, 1 },
            {ConfigData.ProjectileTypes.FlagshipShot, 2 },
            {ConfigData.ProjectileTypes.Rocket, 2 },
            {ConfigData.ProjectileTypes.HumanSmall, 2 },
            {ConfigData.ProjectileTypes.HumanMedium, 2 },
            {ConfigData.ProjectileTypes.Beam, 2 },
            {ConfigData.ProjectileTypes.SplitShot, 1 },
            {ConfigData.ProjectileTypes.QueenSmall, 1 },
            {ConfigData.ProjectileTypes.QueenLarge, 1 },
            {ConfigData.ProjectileTypes.StrikerBomb, 2 },
            {ConfigData.ProjectileTypes.RocketExplosion, 2 },
            {ConfigData.ProjectileTypes.FireBargeExplosion, 2 },
        };
        public static Dictionary<ConfigData.ShipTypes, string> ConvertShipTypeToName = new Dictionary<ConfigData.ShipTypes, string>
        {
            {ConfigData.ShipTypes.Barge, "Barge"},
            {ConfigData.ShipTypes.Beacon, "Beacon"},
            {ConfigData.ShipTypes.Beehive, "Beehive"},
            {ConfigData.ShipTypes.Bumblebee, "Bumblebee"},
            {ConfigData.ShipTypes.CarpenterBee, "Carpenter Bee"},
            {ConfigData.ShipTypes.Carrier, "Carrier"},
            {ConfigData.ShipTypes.Cruiser, "Cruiser"},
            {ConfigData.ShipTypes.Dreadnought, "Dreadnought"},
            {ConfigData.ShipTypes.Drone, "Drone"},
            {ConfigData.ShipTypes.Factory, "Factory"},
            {ConfigData.ShipTypes.FireBarge, "Fire Barge"},
            {ConfigData.ShipTypes.Flagship, "Flagship"},
            {ConfigData.ShipTypes.Frigate, "Frigate"},
            {ConfigData.ShipTypes.Gunship, "Gunship"},
            {ConfigData.ShipTypes.Honeybee, "Honeybee"},
            {ConfigData.ShipTypes.Hornet, "Hornet"},
            {ConfigData.ShipTypes.Leafcutter, "Leafcutter"},
            {ConfigData.ShipTypes.Queen, "Queen"},
            {ConfigData.ShipTypes.Scout, "Scout"},
            {ConfigData.ShipTypes.Striker, "Striker"},
            {ConfigData.ShipTypes.WarpGate, "Warp Gate"},
            {ConfigData.ShipTypes.Wasp, "Wasp"},
            {ConfigData.ShipTypes.YellowJacket, "Yellow Jacket"},
        };

        public static Dictionary<string, ConfigData.ShipTypes> ConvertShipNameToShipType = new Dictionary<string, ConfigData.ShipTypes>
        {
            {"Barge", ConfigData.ShipTypes.Barge},
            {"Beacon", ConfigData.ShipTypes.Beacon},
            {"Beehive", ConfigData.ShipTypes.Beehive},
            {"Bumblebee", ConfigData.ShipTypes.Bumblebee},
            {"Carpenter Bee", ConfigData.ShipTypes.CarpenterBee},
            {"Carrier", ConfigData.ShipTypes.Carrier},
            {"Cruiser", ConfigData.ShipTypes.Cruiser},
            {"Dreadnought", ConfigData.ShipTypes.Dreadnought},
            {"Drone", ConfigData.ShipTypes.Drone},
            {"Factory", ConfigData.ShipTypes.Factory},
            {"Fire Barge", ConfigData.ShipTypes.FireBarge},
            {"Flagship", ConfigData.ShipTypes.Flagship},
            {"Frigate", ConfigData.ShipTypes.Frigate},
            {"Gunship", ConfigData.ShipTypes.Gunship},
            {"Honeybee", ConfigData.ShipTypes.Honeybee},
            {"Hornet", ConfigData.ShipTypes.Hornet},
            {"Leafcutter", ConfigData.ShipTypes.Leafcutter},
            {"Queen", ConfigData.ShipTypes.Queen},
            {"Scout", ConfigData.ShipTypes.Scout},
            {"Striker", ConfigData.ShipTypes.Striker},
            {"Warp Gate", ConfigData.ShipTypes.WarpGate},
            {"Wasp", ConfigData.ShipTypes.Wasp},
            {"Yellow Jacket", ConfigData.ShipTypes.YellowJacket},
        };

        public static Dictionary<string, ConfigData.ShipTypes> ConvertPluralNameToShipType = new Dictionary<string, ConfigData.ShipTypes>
        {
            {"Barges", ConfigData.ShipTypes.Barge},
            {"Beacons", ConfigData.ShipTypes.Beacon},
            {"Beehives", ConfigData.ShipTypes.Beehive},
            {"Bumblebees", ConfigData.ShipTypes.Bumblebee},
            {"Carpenter Bees", ConfigData.ShipTypes.CarpenterBee},
            {"Carriers", ConfigData.ShipTypes.Carrier},
            {"Cruisers", ConfigData.ShipTypes.Cruiser},
            {"Dreadnoughts", ConfigData.ShipTypes.Dreadnought},
            {"Drones", ConfigData.ShipTypes.Drone},
            {"Factories", ConfigData.ShipTypes.Factory},
            {"Fire Barges", ConfigData.ShipTypes.FireBarge},
            {"Flagships", ConfigData.ShipTypes.Flagship},
            {"Frigates", ConfigData.ShipTypes.Frigate},
            {"Gunships", ConfigData.ShipTypes.Gunship},
            {"Honeybees", ConfigData.ShipTypes.Honeybee},
            {"Hornets", ConfigData.ShipTypes.Hornet},
            {"Leafcutters", ConfigData.ShipTypes.Leafcutter},
            {"Queen", ConfigData.ShipTypes.Queen},
            {"Scouts", ConfigData.ShipTypes.Scout},
            {"Strikers", ConfigData.ShipTypes.Striker},
            {"Warp Gates", ConfigData.ShipTypes.WarpGate},
            {"Wasps", ConfigData.ShipTypes.Wasp},
            {"Yellow Jackets", ConfigData.ShipTypes.YellowJacket},
        };

        public static Dictionary<ConfigData.ShipTypes, string> ConvertShipTypeToPluralName = new Dictionary<ConfigData.ShipTypes, string>
        {
            {ConfigData.ShipTypes.Barge, "Barges"},
            {ConfigData.ShipTypes.Beacon, "Beacons"},
            {ConfigData.ShipTypes.Beehive, "Beehives"},
            {ConfigData.ShipTypes.Bumblebee, "Bumblebees"},
            {ConfigData.ShipTypes.CarpenterBee, "Carpenter Bees"},
            {ConfigData.ShipTypes.Carrier, "Carriers"},
            {ConfigData.ShipTypes.Cruiser, "Cruisers"},
            {ConfigData.ShipTypes.Dreadnought, "Dreadnoughts"},
            {ConfigData.ShipTypes.Drone, "Drones"},
            {ConfigData.ShipTypes.Factory, "Factories"},
            {ConfigData.ShipTypes.FireBarge, "Fire Barges"},
            {ConfigData.ShipTypes.Flagship, "Flagships"},
            {ConfigData.ShipTypes.Frigate, "Frigates"},
            {ConfigData.ShipTypes.Gunship, "Gunships"},
            {ConfigData.ShipTypes.Honeybee, "Honeybees"},
            {ConfigData.ShipTypes.Hornet, "Hornets"},
            {ConfigData.ShipTypes.Leafcutter, "Leafcutters"},
            {ConfigData.ShipTypes.Queen, "Queens"},
            {ConfigData.ShipTypes.Scout, "Scouts"},
            {ConfigData.ShipTypes.Striker, "Strikers"},
            {ConfigData.ShipTypes.WarpGate, "Warp Gates"},
            {ConfigData.ShipTypes.Wasp, "Wasps"},
            {ConfigData.ShipTypes.YellowJacket, "Yellow Jackets"},
        };

        public static Dictionary<string, ConfigData.CommandTypes> ConvertCommandNameToType = new Dictionary<string, ConfigData.CommandTypes>
        {
            {"Aggressive", ConfigData.CommandTypes.Aggressive },
            {"Bombing Run", ConfigData.CommandTypes.BombingRun },
            {"Charge", ConfigData.CommandTypes.Charge },
            {"Defensive", ConfigData.CommandTypes.Defensive },
            {"Random", ConfigData.CommandTypes.Random },
            {"Circle", ConfigData.CommandTypes.Circle },
            {"Right Swipe", ConfigData.CommandTypes.RightSwipe },
            {"Left Swipe", ConfigData.CommandTypes.LeftSwipe },
            {"Closest Friendly", ConfigData.CommandTypes.ClosestFriendly },
            {"In and Out", ConfigData.CommandTypes.InAndOut },
            {"Patrol", ConfigData.CommandTypes.Patrol },
            {"Guard", ConfigData.CommandTypes.Guard },
            {"Scouting", ConfigData.CommandTypes.Scouting },
            {"Mining", ConfigData.CommandTypes.Mining },
            {"Full Retreat", ConfigData.CommandTypes.FullRetreat },
        };

        public static Dictionary<ConfigData.CommandTypes, string> ConvertCommandTypeToName = new Dictionary<ConfigData.CommandTypes, string>
        {
            {ConfigData.CommandTypes.Aggressive, "Aggressive" },
            {ConfigData.CommandTypes.BombingRun, "Bombing Run" },
            {ConfigData.CommandTypes.Charge, "Charge" },
            {ConfigData.CommandTypes.Defensive, "Defensive" },
            {ConfigData.CommandTypes.Random, "Random" },
            {ConfigData.CommandTypes.Circle, "Circle" },
            {ConfigData.CommandTypes.RightSwipe, "Right Swipe" },
            {ConfigData.CommandTypes.LeftSwipe, "Left Swipe" },
            {ConfigData.CommandTypes.ClosestFriendly, "Closest Friendly" },
            {ConfigData.CommandTypes.InAndOut, "In and Out" },
            {ConfigData.CommandTypes.Patrol, "Patrol" },
            {ConfigData.CommandTypes.Guard, "Guard" },
            {ConfigData.CommandTypes.Scouting, "Scouting" },
            {ConfigData.CommandTypes.Mining, "Mining" },
            {ConfigData.CommandTypes.FullRetreat, "Full Retreat" },
        };
        public static Dictionary<ConfigData.MatchupStrategyTypes, ConfigData.ShipTypes> ConvertMatchupStrategyToShipType = new Dictionary<ConfigData.MatchupStrategyTypes, ConfigData.ShipTypes>
        {
            { ConfigData.MatchupStrategyTypes.TypeA, ConfigData.ShipTypes.Queen },
            { ConfigData.MatchupStrategyTypes.TypeB, ConfigData.ShipTypes.Hornet },
            { ConfigData.MatchupStrategyTypes.TypeC, ConfigData.ShipTypes.Dreadnought },
            { ConfigData.MatchupStrategyTypes.TypeD, ConfigData.ShipTypes.Gunship },
            { ConfigData.MatchupStrategyTypes.TypeE, ConfigData.ShipTypes.Scout },
            { ConfigData.MatchupStrategyTypes.TypeF, ConfigData.ShipTypes.Wasp },
            { ConfigData.MatchupStrategyTypes.TypeG, ConfigData.ShipTypes.Bumblebee },
            { ConfigData.MatchupStrategyTypes.TypeH, ConfigData.ShipTypes.Flagship },
            { ConfigData.MatchupStrategyTypes.TypeI, ConfigData.ShipTypes.Honeybee },
            { ConfigData.MatchupStrategyTypes.TypeJ, ConfigData.ShipTypes.CarpenterBee },
            { ConfigData.MatchupStrategyTypes.TypeK, ConfigData.ShipTypes.Leafcutter },
            { ConfigData.MatchupStrategyTypes.TypeL, ConfigData.ShipTypes.YellowJacket },
            { ConfigData.MatchupStrategyTypes.TypeM, ConfigData.ShipTypes.Beehive },
            { ConfigData.MatchupStrategyTypes.TypeN, ConfigData.ShipTypes.Frigate },
            { ConfigData.MatchupStrategyTypes.TypeO, ConfigData.ShipTypes.Carrier },
            { ConfigData.MatchupStrategyTypes.TypeP, ConfigData.ShipTypes.Drone },
            { ConfigData.MatchupStrategyTypes.TypeQ, ConfigData.ShipTypes.Striker },
            { ConfigData.MatchupStrategyTypes.TypeR, ConfigData.ShipTypes.Factory },
            { ConfigData.MatchupStrategyTypes.TypeS, ConfigData.ShipTypes.Cruiser },
            { ConfigData.MatchupStrategyTypes.TypeT, ConfigData.ShipTypes.Barge },
            { ConfigData.MatchupStrategyTypes.TypeU, ConfigData.ShipTypes.FireBarge },
            { ConfigData.MatchupStrategyTypes.TypeV, ConfigData.ShipTypes.WarpGate },
            { ConfigData.MatchupStrategyTypes.TypeW, ConfigData.ShipTypes.Beacon },

        };

        public static Dictionary<string, ConfigData.MatchupStrategyTypes> ConvertMatchupStrategyNameToType = new Dictionary<string, ConfigData.MatchupStrategyTypes>
        {
            { "Random", ConfigData.MatchupStrategyTypes.Random },
            { "Revenge", ConfigData.MatchupStrategyTypes.Revenge },
            { "Most Dangerous", ConfigData.MatchupStrategyTypes.MostDangerous },
            { "Least Health", ConfigData.MatchupStrategyTypes.LeastHealth },
            { "Most Health", ConfigData.MatchupStrategyTypes.MostHealth },
            { "Most Powerful", ConfigData.MatchupStrategyTypes.MostPowerful },
            { "Least Powerful", ConfigData.MatchupStrategyTypes.LeastPowerful },
            { "Closest", ConfigData.MatchupStrategyTypes.Closest },
            { "Furthest", ConfigData.MatchupStrategyTypes.Furthest },
            { "Most Range", ConfigData.MatchupStrategyTypes.MostRange },
            { "Least Range", ConfigData.MatchupStrategyTypes.LeastRange },
            { "Fastest", ConfigData.MatchupStrategyTypes.Fastest },
            { "Slowest", ConfigData.MatchupStrategyTypes.Slowest },
            { "In Combat", ConfigData.MatchupStrategyTypes.InCombat },
            { "Gang Up", ConfigData.MatchupStrategyTypes.GangUp },
            { "Most Valuable", ConfigData.MatchupStrategyTypes.MostValuable },
            { "Least Valuable", ConfigData.MatchupStrategyTypes.LeastValuable },
            { "Type A", ConfigData.MatchupStrategyTypes.TypeA },
            { "Type B", ConfigData.MatchupStrategyTypes.TypeB },
            { "Type C", ConfigData.MatchupStrategyTypes.TypeC },
            { "Type D", ConfigData.MatchupStrategyTypes.TypeD },
            { "Type E", ConfigData.MatchupStrategyTypes.TypeE },
            { "Type F", ConfigData.MatchupStrategyTypes.TypeF },
            { "Type G", ConfigData.MatchupStrategyTypes.TypeG },
            { "Type H", ConfigData.MatchupStrategyTypes.TypeH },
            { "Type I", ConfigData.MatchupStrategyTypes.TypeI },
            { "Type J", ConfigData.MatchupStrategyTypes.TypeJ },
            { "Type K", ConfigData.MatchupStrategyTypes.TypeK },
            { "Type L", ConfigData.MatchupStrategyTypes.TypeL },
            { "Type M", ConfigData.MatchupStrategyTypes.TypeM },
            { "Type N", ConfigData.MatchupStrategyTypes.TypeN },
            { "Type O", ConfigData.MatchupStrategyTypes.TypeO },
            { "Type P", ConfigData.MatchupStrategyTypes.TypeP },
            { "Type Q", ConfigData.MatchupStrategyTypes.TypeQ },
            { "Type R", ConfigData.MatchupStrategyTypes.TypeR },
            { "Type S", ConfigData.MatchupStrategyTypes.TypeS },
            { "Type T", ConfigData.MatchupStrategyTypes.TypeT },
            { "Type U", ConfigData.MatchupStrategyTypes.TypeU },
            { "Type V", ConfigData.MatchupStrategyTypes.TypeV },
            { "Type W", ConfigData.MatchupStrategyTypes.TypeW },


        };

        public static Dictionary<ConfigData.ShootingStrategyTypes, ConfigData.ShipTypes> ConvertShootingStrategyToShipType = new Dictionary<ConfigData.ShootingStrategyTypes, ConfigData.ShipTypes>
        {
            { ConfigData.ShootingStrategyTypes.TypeA, ConfigData.ShipTypes.Queen },
            { ConfigData.ShootingStrategyTypes.TypeB, ConfigData.ShipTypes.Hornet },
            { ConfigData.ShootingStrategyTypes.TypeC, ConfigData.ShipTypes.Dreadnought },
            { ConfigData.ShootingStrategyTypes.TypeD, ConfigData.ShipTypes.Gunship },
            { ConfigData.ShootingStrategyTypes.TypeE, ConfigData.ShipTypes.Scout },
            { ConfigData.ShootingStrategyTypes.TypeF, ConfigData.ShipTypes.Wasp },
            { ConfigData.ShootingStrategyTypes.TypeG, ConfigData.ShipTypes.Bumblebee },
            { ConfigData.ShootingStrategyTypes.TypeH, ConfigData.ShipTypes.Flagship },
            { ConfigData.ShootingStrategyTypes.TypeI, ConfigData.ShipTypes.Honeybee },
            { ConfigData.ShootingStrategyTypes.TypeJ, ConfigData.ShipTypes.CarpenterBee },
            { ConfigData.ShootingStrategyTypes.TypeK, ConfigData.ShipTypes.Leafcutter },
            { ConfigData.ShootingStrategyTypes.TypeL, ConfigData.ShipTypes.YellowJacket },
            { ConfigData.ShootingStrategyTypes.TypeM, ConfigData.ShipTypes.Beehive },
            { ConfigData.ShootingStrategyTypes.TypeN, ConfigData.ShipTypes.Frigate },
            { ConfigData.ShootingStrategyTypes.TypeO, ConfigData.ShipTypes.Carrier },
            { ConfigData.ShootingStrategyTypes.TypeP, ConfigData.ShipTypes.Drone },
            { ConfigData.ShootingStrategyTypes.TypeQ, ConfigData.ShipTypes.Striker },
            { ConfigData.ShootingStrategyTypes.TypeR, ConfigData.ShipTypes.Factory },
            { ConfigData.ShootingStrategyTypes.TypeS, ConfigData.ShipTypes.Cruiser },
            { ConfigData.ShootingStrategyTypes.TypeT, ConfigData.ShipTypes.Barge },
            { ConfigData.ShootingStrategyTypes.TypeU, ConfigData.ShipTypes.FireBarge },
            { ConfigData.ShootingStrategyTypes.TypeV, ConfigData.ShipTypes.WarpGate },
            { ConfigData.ShootingStrategyTypes.TypeW, ConfigData.ShipTypes.Beacon },

        };

        public static Dictionary<string, ConfigData.ShootingStrategyTypes> ConvertShootingStrategyNameToType = new Dictionary<string, ConfigData.ShootingStrategyTypes>
        {
            { "First Seen", ConfigData.ShootingStrategyTypes.FirstSeen}, 
            { "Random", ConfigData.ShootingStrategyTypes.Random},
            { "Revenge", ConfigData.ShootingStrategyTypes.Revenge},
            { "Most Dangerous", ConfigData.ShootingStrategyTypes.MostDangerous},
            { "Most Health", ConfigData.ShootingStrategyTypes.MostHealth},
            { "Least Health", ConfigData.ShootingStrategyTypes.LeastHealth},
            { "Most Powerful", ConfigData.ShootingStrategyTypes.MostPowerful},
            { "Least Powerful", ConfigData.ShootingStrategyTypes.LeastPowerful},
            { "Closest", ConfigData.ShootingStrategyTypes.Closest},
            { "Furthest", ConfigData.ShootingStrategyTypes.Furthest},
            { "Most Range", ConfigData.ShootingStrategyTypes.MostRange},
            { "Least Range", ConfigData.ShootingStrategyTypes.LeastRange},
            { "Fastest", ConfigData.ShootingStrategyTypes.Fastest},
            { "Slowest", ConfigData.ShootingStrategyTypes.Slowest},
            { "Most Valuable", ConfigData.ShootingStrategyTypes.MostValuable},
            { "Least Valuable", ConfigData.ShootingStrategyTypes.LeastValuable}, 
            { "Type A", ConfigData.ShootingStrategyTypes.TypeA},
            { "Type B", ConfigData.ShootingStrategyTypes.TypeB},
            { "Type C", ConfigData.ShootingStrategyTypes.TypeC},
            { "Type D", ConfigData.ShootingStrategyTypes.TypeD},
            { "Type E", ConfigData.ShootingStrategyTypes.TypeE},
            { "Type F", ConfigData.ShootingStrategyTypes.TypeF},
            { "Type G", ConfigData.ShootingStrategyTypes.TypeG},
            { "Type H", ConfigData.ShootingStrategyTypes.TypeH},
            { "Type I", ConfigData.ShootingStrategyTypes.TypeI},
            { "Type J", ConfigData.ShootingStrategyTypes.TypeJ},
            { "Type K", ConfigData.ShootingStrategyTypes.TypeK},
            { "Type L", ConfigData.ShootingStrategyTypes.TypeL},
            { "Type M", ConfigData.ShootingStrategyTypes.TypeM},
            { "Type N", ConfigData.ShootingStrategyTypes.TypeN},
            { "Type O", ConfigData.ShootingStrategyTypes.TypeO},
            { "Type P", ConfigData.ShootingStrategyTypes.TypeP},
            { "Type Q", ConfigData.ShootingStrategyTypes.TypeQ},
            { "Type R", ConfigData.ShootingStrategyTypes.TypeR},
            { "Type S", ConfigData.ShootingStrategyTypes.TypeS},
            { "Type T", ConfigData.ShootingStrategyTypes.TypeT},
            { "Type U", ConfigData.ShootingStrategyTypes.TypeU},
            { "Type V", ConfigData.ShootingStrategyTypes.TypeV},
            { "Type W", ConfigData.ShootingStrategyTypes.TypeW}
        };

        public static Dictionary<ConfigData.ShootingStrategyTypes, string> ConvertShootingStrategyTypeToName = new Dictionary<ConfigData.ShootingStrategyTypes, string>
        {
            { ConfigData.ShootingStrategyTypes.FirstSeen, "First Seen" },
            { ConfigData.ShootingStrategyTypes.Random, "Random" },
            { ConfigData.ShootingStrategyTypes.Revenge, "Revenge" },
            { ConfigData.ShootingStrategyTypes.MostDangerous, "Most Dangerous" },
            { ConfigData.ShootingStrategyTypes.MostHealth, "Most Health" },
            { ConfigData.ShootingStrategyTypes.LeastHealth, "Least Health" },
            { ConfigData.ShootingStrategyTypes.MostPowerful, "Most Powerful" },
            { ConfigData.ShootingStrategyTypes.LeastPowerful, "Least Powerful" },
            { ConfigData.ShootingStrategyTypes.Closest, "Closest" },
            { ConfigData.ShootingStrategyTypes.Furthest, "Furthest" },
            { ConfigData.ShootingStrategyTypes.MostRange, "Most Range" },
            { ConfigData.ShootingStrategyTypes.LeastRange, "Least Range" },
            { ConfigData.ShootingStrategyTypes.Fastest, "Fastest" },
            { ConfigData.ShootingStrategyTypes.Slowest, "Slowest" },
            { ConfigData.ShootingStrategyTypes.MostValuable, "Most Valuable" },
            { ConfigData.ShootingStrategyTypes.LeastValuable, "Least Valuable" },
            { ConfigData.ShootingStrategyTypes.TypeA, "Type A" },
            { ConfigData.ShootingStrategyTypes.TypeB, "Type B" },
            { ConfigData.ShootingStrategyTypes.TypeC, "Type C" },
            { ConfigData.ShootingStrategyTypes.TypeD, "Type D" },
            { ConfigData.ShootingStrategyTypes.TypeE, "Type E" },
            { ConfigData.ShootingStrategyTypes.TypeF, "Type F" },
            { ConfigData.ShootingStrategyTypes.TypeG, "Type G" },
            { ConfigData.ShootingStrategyTypes.TypeH, "Type H" },
            { ConfigData.ShootingStrategyTypes.TypeI, "Type I" },
            { ConfigData.ShootingStrategyTypes.TypeJ, "Type J" },
            { ConfigData.ShootingStrategyTypes.TypeK, "Type K" },
            { ConfigData.ShootingStrategyTypes.TypeL, "Type L" },
            { ConfigData.ShootingStrategyTypes.TypeM, "Type M" },
            { ConfigData.ShootingStrategyTypes.TypeN, "Type N" },
            { ConfigData.ShootingStrategyTypes.TypeO, "Type O" },
            { ConfigData.ShootingStrategyTypes.TypeP, "Type P" },
            { ConfigData.ShootingStrategyTypes.TypeQ, "Type Q" },
            { ConfigData.ShootingStrategyTypes.TypeR, "Type R" },
            { ConfigData.ShootingStrategyTypes.TypeS, "Type S" },
            { ConfigData.ShootingStrategyTypes.TypeT, "Type T" },
            { ConfigData.ShootingStrategyTypes.TypeU, "Type U" },
            { ConfigData.ShootingStrategyTypes.TypeV, "Type V" },
            { ConfigData.ShootingStrategyTypes.TypeW, "Type W" }
        };

        public static Dictionary<string, ConfigData.SquadActions> ConvertSquadActionNameToType = new Dictionary<string, ConfigData.SquadActions>
        {
            { "IsMatchingSpeed", ConfigData.SquadActions.IsMatchingSpeed },
            { "CeaseFire", ConfigData.SquadActions.CeaseFire },
            { "Attack on Sight", ConfigData.SquadActions.AttackOnSight },
            { "Patrol", ConfigData.SquadActions.Patrol },
            { "Guard", ConfigData.SquadActions.Guard },
            { "Chase", ConfigData.SquadActions.Chase },
            { "Hold", ConfigData.SquadActions.Hold },
        };

        public static Dictionary<string, ConfigData.WeaponTypes> ConvertWeaponNameToType = new Dictionary<string, ConfigData.WeaponTypes>
        {
            { "Bomb", ConfigData.WeaponTypes.Bomb },
            { "Beam Cannon", ConfigData.WeaponTypes.BeamCannon },
            { "Light Cannon", ConfigData.WeaponTypes.LightCannon },
            { "Turret", ConfigData.WeaponTypes.Turret },
            { "Full Ship Turret", ConfigData.WeaponTypes.FullShipTurret },
            { "Rocket Turret", ConfigData.WeaponTypes.RocketTurret },
            { "Dual Cannon", ConfigData.WeaponTypes.DualCannon },
            { "Eye", ConfigData.WeaponTypes.Eye },
            { "Split Shot", ConfigData.WeaponTypes.SplitShot },
        };

        public static Dictionary<string, ConfigData.ProjectileTypes> ConvertProjectileNameToType = new Dictionary<string, ConfigData.ProjectileTypes>
        {
            { "None", ConfigData.ProjectileTypes.None },
            { "Bee Small", ConfigData.ProjectileTypes.BeeSmall },
            { "Bee Medium", ConfigData.ProjectileTypes.BeeMedium },
            { "Bumblebee Shot", ConfigData.ProjectileTypes.BumblebeeShot },
            { "Flagship Shot", ConfigData.ProjectileTypes.FlagshipShot },
            { "Rocket", ConfigData.ProjectileTypes.Rocket },
            { "Human Small", ConfigData.ProjectileTypes.HumanSmall },
            { "Human Medium", ConfigData.ProjectileTypes.HumanMedium },
            { "Beam", ConfigData.ProjectileTypes.Beam },
            { "Split Shot", ConfigData.ProjectileTypes.SplitShot },
            { "Queen Small", ConfigData.ProjectileTypes.QueenSmall },
            { "Queen Large", ConfigData.ProjectileTypes.QueenLarge },
            { "Striker Bomb", ConfigData.ProjectileTypes.StrikerBomb },
            { "Rocket Explosion", ConfigData.ProjectileTypes.RocketExplosion },
            { "Fire Barge Explosion", ConfigData.ProjectileTypes.FireBargeExplosion },
        };


        private static readonly Random _rnd = new Random();

        public static int Hash()
        {
            return RandomInt(); 
        }
        public static long UniqueHash()
        {
            long hash = RandomLong() * RandomLong();
            while (ConfigData.UsedHashes.Contains(hash))
            {
                Debug.Log($"A duplicate hash was found! {hash}");
                hash = RandomLong() * RandomLong();
            }
            ConfigData.UsedHashes.Add(hash);
            return hash;
        }
        public static void Shuffle<T>(this List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = _rnd.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        public static bool AreVectorsEqual(Vector2 a, Vector2 b)
        {
            return Math.Floor(a.x) == Math.Floor(b.x) && Math.Floor(a.y) == Math.Floor(b.y);
        }
        public static Random GetRandom()
        {
            return _rnd;
        }
        /// <summary>
        /// Returns an integer between 0 (inclusive) and max (exclusive)
        /// </summary>
        public static int RandomInt(int max = int.MaxValue) 
        {
            return _rnd.Next(max);
        }
        public static long RandomLong(int max = int.MaxValue)
        {
            return (long) _rnd.Next(max);
        }
        /// <summary>
        /// Returns a float between 0 (inclusive) and max (exclusive)
        /// </summary>
        public static float RandomFloat(float max)
        {
            return (float) _rnd.NextDouble() * max;
        }
        public static int RandomSign()
        {
            //Debug.Log($"Random sign: {(r > 0 ? 1 : -1)}");
            return RandomInt(2) > 0 ? 1 : -1;
        }
        /// <summary>
        /// Returns a random boolean value
        /// </summary>
        /// <returns></returns>
        public static bool CoinToss()
        {
            return RandomInt(2) == 0;
        }
        // not strictly speaking the maximum and minimum distance, but the max change in x or y
        public static Vector2 RandomCoordinate(Level level, Vector2 position, Vector2 maxDistance, Vector2 minDistance)
        {
            //Debug.Log($"maxDistance: {maxDistance}, minDistance: {minDistance}");
            Vector2 newLocation = Vector2.zero;
            int loops = 0;
            while ((newLocation == Vector2.zero || !VectorInBounds(level, newLocation)) && loops < 35){
                newLocation = new Vector2(position.x + (RandomFloat(maxDistance.x) + minDistance.x) * RandomSign(), position.y + (RandomFloat(maxDistance.y) + minDistance.y) * RandomSign());
                loops++;
            }
            if (loops == 100)
            {
                Debug.Log($"Couldn't find a random coordinate that was in bounds: {newLocation}, {minDistance}, {maxDistance}");
            }
            return newLocation;

        }
        public static bool VectorInBounds(Level level, Vector2 vector)
        {
            return (vector.x > level.MinX && vector.x < level.MaxX && vector.y > level.MinY && vector.y < level.MaxY);
        }
        public static float Random(float max)
        {
            return (float) _rnd.NextDouble() * max;
        }

        /// <summary>
        /// Calculates the angle between two points in radians
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static float AngleBetweenPoints(Vector2 a, Vector2 b)
        {
            return (Mathf.Atan2(a.x - b.x, a.y - b.y));
        }

        public static Vector2 DirectionBetweenPoints(Vector2 a, Vector2 b)
        {
            return (a - b).normalized;
        }

        public static float AngleBetweenThreePoints(Vector2 a, Vector2 b, Vector2 c)
        {
            return AngleBetweenPoints(c, a) - AngleBetweenPoints(b, a);
        }
        
        public static string GenerateCommanderName()
        {
            return $"{GenerateName()} {GenerateName()}";
        }
        public static string GenerateName(int length = 0)
        {
            if (length == 0)
            {
                length = RandomInt(5)+1;
            }
            string[] consonants = { "b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "l", "n", "p", "q", "r", "s", "sh", "zh", "t", "v", "w", "x" };
            string[] vowels = { "a", "e", "i", "o", "u", "ae", "y" };
            string name = "";
            string firstPiece = consonants[_rnd.Next(consonants.Length)];
            firstPiece = firstPiece.Substring(0, 1).ToUpper() + firstPiece.Substring(1);
            name += firstPiece;
            name += vowels[_rnd.Next(vowels.Length)];
            int lettersAdded = 2;
            while (lettersAdded < length)
            {
                name += consonants[_rnd.Next(consonants.Length)];
                lettersAdded++;
                name += vowels[_rnd.Next(vowels.Length)];
                lettersAdded++;
                
            }
            foreach (string word in ConfigData.Configuration.CensoredWords)
            {
                if (name.Contains(word.ToLower()))
                {
                    return GenerateName(length);
                }
            }
            
            return name;
        }
        // These are different from the camera methods because they convert the values rather than the coordinates. The camera will tell you where a unit is on the world or
        // screen and this will tell you the value of the unit
        public static Vector2 WorldUnitsToScreenPixels(Vector2 vector, Camera camera)
        {
            Vector2 baseWorldPoint = camera.WorldToScreenPoint(Vector2.zero);
            Vector2 screenPoint = camera.WorldToScreenPoint(vector);
            return new Vector2(Mathf.Abs(baseWorldPoint.x - screenPoint.x), Mathf.Abs(baseWorldPoint.y - screenPoint.y));
        }
        public static Vector2 ScreenPixelsToWorldUnits(Vector2 vector, Camera camera)
        {
            Vector2 baseWorldPoint = camera.ScreenToWorldPoint(Vector2.zero);
            Vector2 worldPoint = camera.ScreenToWorldPoint(vector);
            return new Vector2(Mathf.Abs(baseWorldPoint.x - worldPoint.x), Mathf.Abs(baseWorldPoint.y - worldPoint.y));
        }
        public static void WriteJsonFile(string contents)
        {
            string path = $"{ConfigData.GetBasePath()}/{Hash()}.json";
            File.WriteAllText(path, contents);
        }
        public static void WriteTextFile(string contents)
        {
            string path = $"{ConfigData.GetBasePath()}/{Hash()}.txt";
            File.WriteAllText(path, contents);
        }
        public static int[] SetChangablePixelsForImage(Color[] colors, Sprite sprite)
        {
            Texture2D sourceTexture = sprite.texture;
            Color[] pixels = sourceTexture.GetPixels();
            List<int> indexes = new List<int>();
            float threshhold = .005f;

            //Debug.Log($"Pixels: {pixels}, {pixels.Length}, {pixels[0]}, color: {colors.Length}, {colors[0]}");

            for (int c = 0; c < colors.Length; c++)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    Vector3 colorWithoutAlpha = new Vector3(colors[c].r, colors[c].g, colors[c].b);
                    Vector3 pixelWithoutAlpha = new Vector3(pixels[i].r, pixels[i].g, pixels[i].b);
                    float distance = Vector3.Distance(pixelWithoutAlpha, colorWithoutAlpha);
                    if (distance < threshhold)
                    {
                        //Debug.Log($"Found matching color {colors[c]} at {i}");
                        indexes.Add(i);
                    }
                    //else
                    //{
                    //    if (pixels[i].a > .99 && pixels[i].g > .01 && i % 10000 == 0)
                    //    {
                    //        Debug.Log($"Color is too far apart: {pixelWithoutAlpha} != {colorWithoutAlpha} at {i}: {distance} > {threshhold}");
                    //    }
                    //}
                }
            }


            return indexes.ToArray();
        }

        public static List<SavedSquad> LoadSquadsFromJson(List<dynamic> jsonSquads)
        {
            List<SavedSquad> savedSquads = new List<SavedSquad>();
            jsonSquads.ForEach((squad) =>
            {
                Color color = new Color((float)squad.Color.r, (float)squad.Color.g, (float)squad.Color.b, (float)squad.Color.a);
                SquadStatBlock Stats = new SquadStatBlock((string)squad.Stats.Commander, (int)squad.Stats.BattlesFought, (int)squad.Stats.BattlesWon,
                    (int)squad.Stats.ShipsLost, (int)squad.Stats.DamageDone, (int)squad.Stats.DamageReceived, (int)squad.Stats.Kills);
                SavedSquad savedSquad = new SavedSquad((int)squad.Id, (int)squad.Side, (string)squad.Name, new Vector2((float)squad.StartingPosition.x, (float)squad.StartingPosition.y),
                    (bool)squad.CeaseFire, (bool)squad.IsMatchingSpeed, ConvertShootingStrategyNameToType[(string)squad.ChosenShootingStrategy], color, Stats);
                //Debug.Log($"Squad ships, {savedSquad.Name}, {squad.Ships}");
                //Vector2 startingPosition = new Vector2(savedSquad.StartingPosition.x, savedSquad.StartingPosition.y);
                List<dynamic> ships = squad.Ships.ToObject<List<dynamic>>();

                ships.ForEach((ship) =>
                {

                    savedSquad.AddShipToSquad(new SquadShip((int)ship.FleetId, ConvertShipNameToShipType[(string)ship.ShipType], new Vector2((float)ship.Offset.x, (float)ship.Offset.y),
                     savedSquad));

                });
                //Debug.Log($"Loaded squad {squad.Name} at {squad.StartingPosition} at before Add Squad call");
                //savedSquad.StartingPosition = startingPosition;
                savedSquads.Add(savedSquad);

            });
            //Debug.Log("Finished loading the squads from list");
            return savedSquads;
        }

        public static IEnumerator CacheSquadCustomSprites(SavedSquad squad, Dictionary<ConfigData.ShipTypes, List<Sprite>> shipPartSprites, string type, Dictionary<ConfigData.ShipTypes, Vector2Int> sizes, Dialogue dialogue = null)
        {
            float start = Time.realtimeSinceStartup;
            if (squad.HasCustomColor)
            {
                Debug.Log($"Saving custom color ({squad.Color}) sprites for {squad.Name}");

                List<SquadShip> squadShips = squad.GetSquadShips();
                for (int i = 0; i < squadShips.Count; i++)
                {
                    SquadShip squadShip = squadShips[i];

                    if (shipPartSprites.ContainsKey(squadShip.ShipType))
                    {
                        Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(squadShip.ShipType);
                        int index = 0;

                        List<Sprite> sprites = shipPartSprites[squadShip.ShipType];
                        for (int j = 0; j < sprites.Count; j++)
                        {
                            Sprite sprite = sprites[j];
                            //Debug.Log($"The current sprite is {sprite.name} which is {j} / {shipPartSprites[squadShip.ShipType].Count} for {squadShip.ShipType}");


                            if (((squadShip.ShipType == ConfigData.ShipTypes.Factory || squadShip.ShipType == ConfigData.ShipTypes.WarpGate) && index > 0) || type == "remains")
                            {
                                int[] changeablePixels = SetChangablePixelsForImage(colors, sprite);
                                //Debug.Log($"Changable pixels for {squadShip.ShipType}: {changeablePixels.Length}");
                                yield return ConfigData.WaitForEndOfFrame;
                                Texture2D sourceTexture = sprite.texture;
                                Color[] pixels = sourceTexture.GetPixels();
                                yield return ConfigData.WaitForEndOfFrame;
                                for (int p = 0; p < changeablePixels.Length; p++)
                                {
                                    squad.Color.a = pixels[changeablePixels[p]].a; // match the alpha value of the source image
                                    pixels[changeablePixels[p]] = squad.Color;
                                }
                                Texture2D changedTexture = new Texture2D(sourceTexture.width, sourceTexture.height);
                                yield return ConfigData.WaitForEndOfFrame;
                                changedTexture.SetPixels(pixels);
                                //yield return ConfigData.WaitForEndOfFrame;
                                //changedTexture.Apply(true);
                                //yield return ConfigData.WaitForEndOfFrame;

                                Vector2Int size = sizes[squadShip.ShipType];
                                int spriteRows = sourceTexture.height / size.y;
                                int spriteColumns = sourceTexture.width / size.x;
                                //Debug.Log($"Each sprite is {size.x} wide and {size.y} tall for a total width of {size.x * spriteColumns} and total height of {size.y * spriteRows} with a source " +
                                //$"texture size of {sourceTexture.width} x {sourceTexture.height}");

                                for (int y = 0; y < spriteRows; y++)
                                {
                                    for (int x = 0; x < spriteColumns; x++)
                                    {
                                        Sprite recolored = Sprite.Create(changedTexture, new Rect(size.x * x, (sourceTexture.height - size.y * y) - size.y, size.x, size.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
                                        //RecoloredSprites[count].name = $"{NamePrefix}_C_{count}";
                                        yield return ConfigData.WaitForEndOfFrame;
                                        try
                                        {
                                            squadShip.GetFleetShip().SaveSpriteToCache(index, type, recolored.texture.GetPixels(size.x * x, (sourceTexture.height - size.y * y) - size.y, size.x, size.y), size, squad.Color);
                                        }
                                        catch (Exception e)
                                        {
                                            Debug.Log($"Error while trying to save cached sprites: {e}");
                                        }
                                        index++;
                                        yield return ConfigData.WaitForEndOfFrame;
                                    }

                                }
                            }
                            else
                            {
                                Vector2Int size = new Vector2Int(sprite.texture.width, sprite.texture.height);

                                int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, sprite);
                                yield return ConfigData.WaitForEndOfFrame;
                                Sprite recolored = Utilities.SetImageColor(squad.Color, sprite, changeablePixels);
                                yield return ConfigData.WaitForEndOfFrame;
                                try
                                {
                                    squadShip.GetFleetShip().SaveSpriteToCache(index, "ship", recolored.texture.GetPixels(), size, squad.Color);
                                }
                                catch (Exception e)
                                {
                                    Debug.Log($"Error while trying to save cached sprites for {squadShip.GetFleetShip().Name}: {e}");
                                }
                                index++;
                                yield return ConfigData.WaitForEndOfFrame;
                            }
                            yield return ConfigData.WaitForEndOfFrame;
                        }

                        yield return ConfigData.WaitForEndOfFrame;
                    }
                    
                }
                yield return ConfigData.WaitForEndOfFrame;
                ConfigData.CurrentShips.SaveFleetData();
            }

            Debug.Log($"Drawing and saving sprites for {squad.Name} took {(Time.realtimeSinceStartup - start)}s");
            if (dialogue != null)
            {
                dialogue.Hide();
            }
        }

        public static List<KeyCode> GetAllKeys()
        {
            List<KeyCode> keysPressed = new List<KeyCode>();
            if (Input.anyKey)
            {
                foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                {
                    if (key < KeyCode.Mouse0 && Input.GetKey(key))
                    {
                        //Debug.Log("Key pressed: " + key);
                        keysPressed.Add(key);
                        //Debug.Log($"Current keys pressed: {Utilities.ListToString(_newKeyCombination)}");
                    }
                }
            }
            return keysPressed;
        }

        public static Sprite SetImageColor(Color color, Sprite sprite, int[] changeablePixels)
        {

            Texture2D sourceTexture = sprite.texture;
            Color[] pixels = sourceTexture.GetPixels();


            for (int i = 0; i < changeablePixels.Length; i++)
            {
                pixels[changeablePixels[i]] = color;
            }
            Texture2D changedTexture = new Texture2D(sourceTexture.width, sourceTexture.height);

            changedTexture.SetPixels(pixels);
            changedTexture.Apply(true);
            //Debug.Log($"width: {dimensions.x}, height: {dimensions.y}");
            return Sprite.Create(changedTexture, new Rect(0, 0, sourceTexture.width, sourceTexture.height), Vector2.one / 2, ConfigData.PixelsPerUnit);
        }

        public static Vector2 ForceBounds(float x, float y, float MaxX, float MaxY, float MinX, float MinY)
        {
            return new Vector2(Mathf.Clamp(x, MinX, MaxX), Mathf.Clamp(y, MinY, MaxY));
        }

        public static Vector2 RotatePointAroundPoint(Vector2 pivot, Vector2 rotatedPoint, float radians)
        {

            float cosAngle = Mathf.Cos(radians);
            float sinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            Vector2 translatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            float rotatedX = translatedVector.x * cosAngle - translatedVector.y * sinAngle;
            float rotatedY = translatedVector.x * sinAngle + translatedVector.y * cosAngle;

            // Translate the rotated vector back to the original position
            Vector2 rotatedVector = new Vector2(rotatedX, rotatedY) + pivot;

            return rotatedVector;
        }
        /// <summary>
        /// Finds the point on a circle between the position given, the angle, and the radius (distance) given
        /// </summary>
        /// <param name="angle"></param>
        /// <param name="distance"></param>
        /// <returns></returns>
        public static Vector2 CirclePoint(float angle, float distance, Vector2 position)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            return new Vector2((position.x + (Mathf.Cos(angle) * distance)), (position.y + (Mathf.Sin(angle) * distance)));
        }
        public static Vector2Int RotateIntPointAroundPoint(Vector2Int pivot, Vector2Int rotatedPoint, float radians)
        {

            double cosAngle = Mathf.Cos(radians);
            double sinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            Vector2Int translatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            double rotatedX = translatedVector.x * cosAngle - translatedVector.y * sinAngle;
            double rotatedY = translatedVector.x * sinAngle + translatedVector.y * cosAngle;

            // Translate the rotated vector back to the original position
            Vector2Int rotatedVector = new Vector2Int(Convert.ToInt32(rotatedX), Convert.ToInt32(rotatedY)) + pivot;

            return rotatedVector;
        }

        /// <summary>
        /// Rotates the game object on this ship the quickest way towards a rotation and returns true once it reaches that rotation. Returns false once it is done rotating
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <param name="rotationSpeed"></param>
        /// <returns></returns>
        public static bool TimedRotation(GameObject entity, float rotation, float rotationSpeed)
        {
            return TimedRotationDifference(entity, rotation, rotationSpeed) == 0;
        }

        public static float TimedRotationDifference(GameObject entity, float rotation, float rotationSpeed)
        {
            float difference = Mathf.DeltaAngle(entity.transform.eulerAngles.z, rotation);
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (difference > 3)
            {
                entity.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed));
                return difference;
            }
            else if (difference < -3)
            {
                entity.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed * -1));
                return difference;
            }
            else
            {
                entity.transform.eulerAngles = new Vector3(0, 0, rotation);
                return 0;
            }
        }
        /// <summary>
        /// Checks if the different in angle between the rotation and the entity is within 3
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        public static bool IsRotatedTowards(GameObject entity, float rotation)
        {
            float difference = Mathf.DeltaAngle(entity.transform.eulerAngles.z, rotation);
            if (difference > 3 || difference < -3)
            {
                return false;
            }
            return true;
        }
        /// <summary>
        /// Checks to see if a game object is rotated towards a rotation or not
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        public static bool IsAimedAt(GameObject entity, float rotation)
        {
            float difference = Mathf.DeltaAngle(entity.transform.eulerAngles.z, rotation);
            float closeEnough = 3;
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (difference > closeEnough)
            {
                return false;
            }
            else if (difference < (0 - closeEnough))
            {
                return false;
            }
            else
            {
                return true;
            }
        }
        public static string ListToString<T>(List<T> list)
        {
            string str = "";
            list.ForEach(r => str += $"{r}, ");
            if (str.Length > 2)
            {
                str = str.Remove(str.Length - 2);
            }
            return str;
        }
        public static void SetUIColor(GameObject gameObject, Color color)
        {
            Image image = gameObject.GetComponent<Image>();
            if (image != null)
            {
                image.color = color;
            }
            else
            {
                SpriteRenderer sprite = gameObject.GetComponent<SpriteRenderer>();
                if (sprite != null)
                {
                    sprite.color = color;
                }
                else
                {
                    Debug.LogError($"Tried to set the color of {gameObject.name} which doesn't have a UI image.");
                }
            }
            
        }
        public static void SetBadColor(GameObject gameObject)
        {
            SetUIColor(gameObject, ConfigData.GetUIColor("bad"));
        }
        public static void SetGoodColor(GameObject gameObject)
        {
            SetUIColor(gameObject, ConfigData.GetUIColor("good"));
        }
        public static string ValidateInputString(string str)
        {
            return Regex.Replace(str, @"[^a-zA-Z0-9\-\s!@#%&*_+=:'.?]", "");
            //Debug.Log($"Unvalidated string: {name}, replaced string {valid}");
        }

        public static string ListToString(List<dynamic> list)
        {
            return string.Join(", ", list.ToArray());
        }
        public static List<T> JArrayToList<T>(dynamic jArray)
        {
           return ((JArray)jArray).ToList<dynamic>().ConvertAll((item) => (T)item);
        }

        public static List<ConfigData.WeaponTypes> JArrayToWeaponTypes(dynamic jArray)
        {
            List<string> weaponList = JArrayToList<string>(jArray);
            return weaponList.ConvertAll((item) => ConvertWeaponNameToType[item]);
        }

        public static List<ConfigData.ProjectileTypes> JArrayToProjectileTypes(dynamic jArray)
        {
            List<string> projectileList = JArrayToList<string>(jArray);
            return projectileList.ConvertAll((item) => ConvertProjectileNameToType[item]);
        }
        public static List<ConfigData.ShipTypes> JArrayToShipTypes(dynamic jArray)
        {
            List<string> ShipList = JArrayToList<string>(jArray);
            return ShipList.ConvertAll((item) => ConvertShipNameToShipType[item]);
        }
        public static Dictionary<K, V> JArrayToDictionary<K, V>(dynamic jArray)
        {
            Dictionary<K, V> dictionary = new Dictionary<K, V>();
            List<dynamic> list = JArrayToList<dynamic>(jArray);
            list.ForEach((item) =>
            {
                Dictionary<K, V> d = ((JObject)item).ToObject<Dictionary<K, V>>();
                dictionary.Add(d.Keys.First(), d.Values.First());
            });
            return dictionary;
        }
        public static Dictionary<ConfigData.ShipTypes, int> JArrayToShipTypeDictionary(dynamic jArray)
        {
            Dictionary<ConfigData.ShipTypes, int> dictionary = new Dictionary<ConfigData.ShipTypes, int>();
            List<dynamic> list = JArrayToList<dynamic>(jArray);
            list.ForEach((item) =>
            {
                Dictionary<string, int> d = ((JObject)item).ToObject<Dictionary<string, int>>();
                dictionary.Add(ConvertShipNameToShipType[d.Keys.First()], d.Values.First());
            });
            return dictionary;
        }
        public static int CalculateCarrierAdditionalTsv()
        {
            FleetShip striker = new FleetShip(-1, "", ConfigData.ShipTypes.Striker, false, false, false, 0, 0, 0, 0, 0, 0, 0);
            FleetShip drone = new FleetShip(-1, "", ConfigData.ShipTypes.Drone, false, false, false, 0, 0, 0, 0, 0, 0, 0);

            return ((striker.GetTsv() * ConfigData.Configuration.CarrierCarryStrikerMax) * ConfigData.Configuration.CarrierSquadCount) + ((drone.GetTsv() * ConfigData.Configuration.CarrierCarryDroneMax) * ConfigData.Configuration.CarrierSquadCount);
        }
        public static int CalculateTsv(Ship ship)
        {
            //Debug.Log($"Calculating TSV for {ship.Name}");
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateMaxTsv(Ship ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateMaxTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateTsv(float speed, float firepower, int health, int sight, int additionalTsv)
        {
            double speedValue = speed / 3;
            int fullHealthTsv = (int)Math.Round((firepower > 0 ? firepower : 1) * (speedValue > 1 ? speedValue : 1) * (math.max(health / 200, 1)), 0) + sight;
            int tsv = ((health > 0 ? 1 : 0) * fullHealthTsv) + ((health > 0 ? 1 : 0) * (health + additionalTsv));
            //Debug.Log($"This ship has speed {speed}, speedValue {speedValue}, firepower {firepower}, health: {health}, sight {sight}, fullHealthTSV {fullHealthTsv}, additionalTSV {additionalTsv}" +
            //    $" to culminate in tsv of {tsv}");
            return tsv;
        }
        public static float CalculateFirepower(int power, int range, float rateOfFire, float rotationRate, float ProjectileValue, float specialFirepower)
        {
            //Debug.Log($"Power: {(power * ProjectileValue)}, DPS: {((power * ProjectileValue) / rateOfFire)}, Range: {Mathf.Pow((range / 20), 2)}");
            return rateOfFire > 0 ? ((((power*ProjectileValue) / rateOfFire) * Mathf.Clamp(rotationRate/128, .5f, 1.25f)) * Mathf.Pow((range / 20), 2)) : specialFirepower;
        }
        /// <summary>
        /// Linecasts from start to end to check for any obstacles in the path. Returns true if there are obstacles in the way
        /// </summary>
        /// <param name="destination"></param>
        /// <returns></returns>
        public static bool HasObstaclesInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstaclesLayerMask).collider != null;
        }
        public static Collider2D GetObstaclesInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstaclesLayerMask).collider;
        }

        /// <summary>
        /// Linecasts from start to end to check for any obstacles in the path. Returns true if there are obstacles or obstacle proximity ranges in the way
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        //public static bool HasObstaclesCloseToInTheWay(Vector2 start, Vector2 end)
        //{
        //    //Collider2D obstacle = Physics2D.Linecast(start, end, ConfigData.ObstacleProximityRangesLayerMask).collider;
        //    //if (obstacle != null)
        //    //{
        //    //    Debug.Log($"There is {obstacle.gameObject.name} in the way between {start} and {end}");
        //    //    return true;
        //    //}
        //    //return false;
        //    //return Physics2D.Linecast(start, end, ConfigData.ObstacleProximityRangesLayerMask).collider != null;
        //}
        public static void Print2DArray(bool[][] array)
        {
            string line = "";
            for (int x = 0; x < array.Length; x++)
            {
                for (int y = 0; y < array[x].Length; y++)
                {
                    line += $"{(array[y][x] ? "▢" : "■")}";
                }
                line += "\n";
            }
            WriteTextFile(line);
        }

        public static void Print2DArrayAsImage(int[][] array)
        {
            //array.Reverse();
            Texture2D texture = new Texture2D(array.Length, array[0].Length, TextureFormat.RGB24, false);
            //Color[] pixels = texture.GetPixels();
            for (int y = 0; y < array[0].Length; y++)
            {
                for (int x = 0; x < array.Length; x++)
                {

                    if (array[x][y] > 0)
                    {

                        texture.SetPixel(x, array[0].Length-(y+1), new Color(0, ((float)array[x][y] / (array.Length / 2)) + .25f, .25f));
                    } 
                    else
                    {
                        texture.SetPixel(x, array[0].Length - (y + 1), ConfigData.GetUIColor("bad"));

                    }
                }
            }
            //Color[] pixels = texture.GetPixels();
            //System.Array.Reverse(pixels, 0, pixels.Length);
            //texture.SetPixels(pixels);
            //texture.Apply();
            string path = $"{ConfigData.GetBasePath()}/{Hash()}.png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }

        public static int GetNegativeFleetshipId()
        {
            return -(Hash() + ConfigData.CurrentShips.GetFleetShips().Count);
        }
        public static int GetNegativeSavedSquadId()
        {
            return -(Hash() + ConfigData.CurrentShips.GetSavedSquads().Count);
        }




    }
}