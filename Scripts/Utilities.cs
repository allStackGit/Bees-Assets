using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
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
            {"Defensive", ConfigData.CommandTypes.Retreat },
            {"Random", ConfigData.CommandTypes.MoveToRandom },
            {"Circle", ConfigData.CommandTypes.CircleSquad },
            {"Right Swipe", ConfigData.CommandTypes.RightSwipe },
            {"Left Swipe", ConfigData.CommandTypes.LeftSwipe },
            {"Closest Friendly", ConfigData.CommandTypes.ClosestFriendly },
            {"In and Out", ConfigData.CommandTypes.InAndOut },
            {"Patrol", ConfigData.CommandTypes.Patrol },
            {"Guard", ConfigData.CommandTypes.Guard },
            {"Scouting", ConfigData.CommandTypes.Scouting },
            {"Mining", ConfigData.CommandTypes.Mining },
            {"Full Retreat", ConfigData.CommandTypes.FullRetreat },
            {"Hold", ConfigData.CommandTypes.Hold },
            {"Heal", ConfigData.CommandTypes.Heal },
        };

        public static Dictionary<ConfigData.CommandTypes, string> ConvertCommandTypeToName = new Dictionary<ConfigData.CommandTypes, string>
        {
            {ConfigData.CommandTypes.Aggressive, "Aggressive" },
            {ConfigData.CommandTypes.BombingRun, "Bombing Run" },
            {ConfigData.CommandTypes.Charge, "Charge" },
            {ConfigData.CommandTypes.Retreat, "Defensive" },
            {ConfigData.CommandTypes.MoveToRandom, "Random" },
            {ConfigData.CommandTypes.CircleSquad, "Circle" },
            {ConfigData.CommandTypes.RightSwipe, "Right Swipe" },
            {ConfigData.CommandTypes.LeftSwipe, "Left Swipe" },
            {ConfigData.CommandTypes.ClosestFriendly, "Closest Friendly" },
            {ConfigData.CommandTypes.InAndOut, "In and Out" },
            {ConfigData.CommandTypes.Patrol, "Patrol" },
            {ConfigData.CommandTypes.Guard, "Guard" },
            {ConfigData.CommandTypes.Scouting, "Scouting" },
            {ConfigData.CommandTypes.Mining, "Mining" },
            {ConfigData.CommandTypes.FullRetreat, "Full Retreat" },
            {ConfigData.CommandTypes.Hold, "Hold" },
            {ConfigData.CommandTypes.Heal, "Heal" },
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
            { "Queen Eye", ConfigData.WeaponTypes.QueenEye },
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

        public static Dictionary<string, ConfigData.RequestTypes> ConvertNameToRequestType = new Dictionary<string, ConfigData.RequestTypes>
        {
            {"get-matchup-strategy", ConfigData.RequestTypes.GetMatchupStrategy },
            {"get-strategy", ConfigData.RequestTypes.GetStrategy },
            {"send-rl-data", ConfigData.RequestTypes.SendRLData },
            {"store-commands", ConfigData.RequestTypes.StoreCommands },
            {"setup-level", ConfigData.RequestTypes.SetupLevel },
            {"reconnect-level", ConfigData.RequestTypes.ReconnectLevel },
            {"get-user-data", ConfigData.RequestTypes.GetUserData },
            {"store-user-data", ConfigData.RequestTypes.StoreUserData },
            {"get-settings", ConfigData.RequestTypes.GetSettings }
        };

        public static Dictionary<ConfigData.RequestTypes, string> ConvertRequestTypeToName = new Dictionary<ConfigData.RequestTypes, string>
        {
            { ConfigData.RequestTypes.GetMatchupStrategy, "get-matchup-strategy" },
            { ConfigData.RequestTypes.GetStrategy, "get-strategy" },
            { ConfigData.RequestTypes.SendRLData, "send-rl-data" },
            { ConfigData.RequestTypes.StoreCommands, "store-commands" },
            { ConfigData.RequestTypes.SetupLevel, "setup-level" },
            { ConfigData.RequestTypes.ReconnectLevel, "reconnect-level" },
            { ConfigData.RequestTypes.GetUserData, "get-user-data" },
            { ConfigData.RequestTypes.StoreUserData, "store-user-data" },
            { ConfigData.RequestTypes.GetSettings, "get-settings" }
        };


        // ===========================================================
        // Class-level fields for Hash methods
        // ===========================================================
        private static readonly Random _rnd = new Random();
        private static long _uniqueHash_tempHash;

        public static long Hash()
        {
            //long hash = UniqueHash();
            //Debug.Log($"Hash: {hash}");
            //return hash;
            return RandomLong(10000000);
            //return UniqueHash();
            //return RandomInt(); 
        }

        /// <summary>
        /// Generates a number that's guarenteed to be unique (for this game load) and is less than 10t
        /// </summary>
        /// <returns></returns>
        public static long UniqueHash()
        {
            _uniqueHash_tempHash = RandomLong(10000000); // If you don't pass 10m in, it could potentially generate a number larger than JS's MAX_SAFE_INT
            while (ConfigData.UsedHashes.Contains(_uniqueHash_tempHash))
            {
                Debug.LogWarning($"A duplicate hash was found! {_uniqueHash_tempHash} There are {ConfigData.UsedHashes.Count} unique hashes stored");
                _uniqueHash_tempHash = RandomLong(10000000);
            }
            ConfigData.UsedHashes.Add(_uniqueHash_tempHash);
            return _uniqueHash_tempHash;
        }
        // ===========================================================
        // Class-level fields for Shuffle method
        // ===========================================================
        private static int _shuffle_n;
        private static int _shuffle_k;
        private static object _shuffle_value;

        public static void Shuffle<T>(this List<T> list)
        {
            _shuffle_n = list.Count;
            while (_shuffle_n > 1)
            {
                _shuffle_n--;
                _shuffle_k = _rnd.Next(_shuffle_n + 1);
                _shuffle_value = list[_shuffle_k];
                list[_shuffle_k] = list[_shuffle_n];
                list[_shuffle_n] = (T)_shuffle_value;
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
        /// <summary>
        /// Generates a long by multiplying to random numbers no larger than max
        /// </summary>
        /// <param name="max"></param>
        /// <returns></returns>
        public static long RandomLong(int max = int.MaxValue)
        {
            return (long) _rnd.Next(max) * _rnd.Next(max);
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
        // ===========================================================
        // Class-level fields for RandomCoordinate method
        // ===========================================================
        private static Vector2 _randomCoordinate_newLocation;
        private static int _randomCoordinate_loops;

        public static Vector2 RandomCoordinate(Level level, Vector2 position, Vector2 maxDistance, Vector2 minDistance)
        {
            _randomCoordinate_newLocation = Vector2.zero;
            _randomCoordinate_loops = 0;
            while ((_randomCoordinate_newLocation == Vector2.zero || !VectorInBounds(level, _randomCoordinate_newLocation)) && _randomCoordinate_loops < 35)
            {
                _randomCoordinate_newLocation = new Vector2(
                    position.x + (RandomFloat(maxDistance.x) + minDistance.x) * RandomSign(),
                    position.y + (RandomFloat(maxDistance.y) + minDistance.y) * RandomSign()
                );
                _randomCoordinate_loops++;
            }
            if (_randomCoordinate_loops == 100)
            {
                Debug.Log($"Couldn't find a random coordinate that was in bounds: {_randomCoordinate_newLocation}, {minDistance}, {maxDistance}");
            }
            return _randomCoordinate_newLocation;
        }

        public static bool VectorInBounds(Level level, Vector2 vector)
        {
            return (vector.x > level.MinX && vector.x < level.MaxX && vector.y > level.MinY && vector.y < level.MaxY);
        }

        public static float AngleBetweenPoints(Vector2 a, Vector2 b)
        {
            return Mathf.Atan2(a.x - b.x, a.y - b.y);
        }

        public static Vector2 DirectionBetweenPoints(Vector2 a, Vector2 b)
        {
            return (a - b).normalized;
        }

        public static float AngleBetweenThreePoints(Vector2 a, Vector2 b, Vector2 c)
        {
            return AngleBetweenPoints(c, a) - AngleBetweenPoints(b, a);
        }

        // ===========================================================
        // Class-level fields for GenerateName methods
        // ===========================================================
        private static string[] _generateName_consonants = { "b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "l", "n", "p", "q", "r", "s", "sh", "zh", "t", "v", "w", "x" };
        private static string[] _generateName_vowels = { "a", "e", "i", "o", "u", "ae", "y" };
        private static string _generateName_result;
        private static string _generateName_firstPiece;
        private static int _generateName_lettersAdded;

        public static string GenerateCommanderName()
        {
            return $"{GenerateName()} {GenerateName()}";
        }

        public static string GenerateName(int length = 0)
        {
            if (length == 0)
            {
                length = RandomInt(5) + 1;
            }
            _generateName_result = "";
            _generateName_firstPiece = _generateName_consonants[_rnd.Next(_generateName_consonants.Length)];
            _generateName_firstPiece = _generateName_firstPiece.Substring(0, 1).ToUpper() + _generateName_firstPiece.Substring(1);
            _generateName_result += _generateName_firstPiece;
            _generateName_result += _generateName_vowels[_rnd.Next(_generateName_vowels.Length)];
            _generateName_lettersAdded = 2;
            while (_generateName_lettersAdded < length)
            {
                _generateName_result += _generateName_consonants[_rnd.Next(_generateName_consonants.Length)];
                _generateName_lettersAdded++;
                _generateName_result += _generateName_vowels[_rnd.Next(_generateName_vowels.Length)];
                _generateName_lettersAdded++;
            }
            foreach (string word in ConfigData.Configuration.CensoredWords)
            {
                if (_generateName_result.Contains(word.ToLower()))
                {
                    return GenerateName(length);
                }
            }
            return _generateName_result;
        }

        // Static WorldUnitsToScreenPixels method variables
        private static Vector2 worldUnitsToScreenPixels_baseWorldPoint;
        private static Vector2 worldUnitsToScreenPixels_screenPoint;

        // Static ScreenPixelsToWorldUnits method variables
        private static Vector2 screenPixelsToWorldUnits_baseWorldPoint;
        private static Vector2 screenPixelsToWorldUnits_worldPoint;

        // Static WriteJsonFile method variables
        private static string writeJsonFile_path;

        // Static WriteTextFile method variables
        private static string writeTextFile_path;

        public static Vector2 WorldUnitsToScreenPixels(Vector2 vector, Camera camera)
        {
            // Assign static variables
            worldUnitsToScreenPixels_baseWorldPoint = camera.WorldToScreenPoint(Vector2.zero);
            worldUnitsToScreenPixels_screenPoint = camera.WorldToScreenPoint(vector);

            return new Vector2(Mathf.Abs(worldUnitsToScreenPixels_baseWorldPoint.x - worldUnitsToScreenPixels_screenPoint.x),
                               Mathf.Abs(worldUnitsToScreenPixels_baseWorldPoint.y - worldUnitsToScreenPixels_screenPoint.y));
        }

        public static Vector2 ScreenPixelsToWorldUnits(Vector2 vector, Camera camera)
        {
            // Assign static variables
            screenPixelsToWorldUnits_baseWorldPoint = camera.ScreenToWorldPoint(Vector2.zero);
            screenPixelsToWorldUnits_worldPoint = camera.ScreenToWorldPoint(vector);

            return new Vector2(Mathf.Abs(screenPixelsToWorldUnits_baseWorldPoint.x - screenPixelsToWorldUnits_worldPoint.x),
                               Mathf.Abs(screenPixelsToWorldUnits_baseWorldPoint.y - screenPixelsToWorldUnits_worldPoint.y));
        }

        public static void WriteJsonFile(string contents)
        {
            // Assign static variable
            writeJsonFile_path = $"{ConfigData.GetBasePath()}/{Hash()}.json";
            File.WriteAllText(writeJsonFile_path, contents);
        }

        public static void WriteTextFile(string contents)
        {
            // Assign static variable
            writeTextFile_path = $"{ConfigData.GetBasePath()}/{Hash()}.txt";
            File.WriteAllText(writeTextFile_path, contents);
        }
        // --- SetChangablePixelsForImage Method Variables ---
        // Local variables for SetChangablePixelsForImage
        private static Texture2D sourceTexture_SetChangablePixelsForImage;
        private static Color[] pixels_SetChangablePixelsForImage;
        private static List<int> indexes_SetChangablePixelsForImage;
        private static float threshhold_SetChangablePixelsForImage;
        private static int c_SetChangablePixelsForImage;
        private static int i_SetChangablePixelsForImage;
        private static Vector3 colorWithoutAlpha_SetChangablePixelsForImage;
        private static Vector3 pixelWithoutAlpha_SetChangablePixelsForImage;
        private static float distance_SetChangablePixelsForImage;

        public static int[] SetChangablePixelsForImage(Color[] colors, Sprite sprite)
        {
            // Declare all local variables as class-level static variables
            sourceTexture_SetChangablePixelsForImage = sprite.texture;
            pixels_SetChangablePixelsForImage = sourceTexture_SetChangablePixelsForImage.GetPixels();
            indexes_SetChangablePixelsForImage = new List<int>();
            threshhold_SetChangablePixelsForImage = .005f;

            // Loop through the colors and pixels to find matching colors
            for (c_SetChangablePixelsForImage = 0; c_SetChangablePixelsForImage < colors.Length; c_SetChangablePixelsForImage++)
            {
                for (i_SetChangablePixelsForImage = 0; i_SetChangablePixelsForImage < pixels_SetChangablePixelsForImage.Length; i_SetChangablePixelsForImage++)
                {
                    colorWithoutAlpha_SetChangablePixelsForImage = new Vector3(colors[c_SetChangablePixelsForImage].r, colors[c_SetChangablePixelsForImage].g, colors[c_SetChangablePixelsForImage].b);
                    pixelWithoutAlpha_SetChangablePixelsForImage = new Vector3(pixels_SetChangablePixelsForImage[i_SetChangablePixelsForImage].r, pixels_SetChangablePixelsForImage[i_SetChangablePixelsForImage].g, pixels_SetChangablePixelsForImage[i_SetChangablePixelsForImage].b);
                    distance_SetChangablePixelsForImage = Vector3.Distance(pixelWithoutAlpha_SetChangablePixelsForImage, colorWithoutAlpha_SetChangablePixelsForImage);
                    if (distance_SetChangablePixelsForImage < threshhold_SetChangablePixelsForImage)
                    {
                        indexes_SetChangablePixelsForImage.Add(i_SetChangablePixelsForImage);
                    }
                }
            }

            return indexes_SetChangablePixelsForImage.ToArray();
        }

        // Class-level static variables for CacheSquadCustomSprites method
        private static float cacheSquadStartTime; // Method: CacheSquadCustomSprites
        private static List<SquadShip> cacheSquadShips; // Method: CacheSquadCustomSprites
        private static SquadShip cacheSquadShip; // Method: CacheSquadCustomSprites
        private static Color[] cacheSquadColors; // Method: CacheSquadCustomSprites
        private static int cacheSquadIndex; // Method: CacheSquadCustomSprites
        private static List<Sprite> cacheSquadSprites; // Method: CacheSquadCustomSprites
        private static Sprite cacheSquadSprite; // Method: CacheSquadCustomSprites
        private static int[] cacheSquadChangeablePixels; // Method: CacheSquadCustomSprites
        private static Texture2D cacheSquadSourceTexture; // Method: CacheSquadCustomSprites
        private static Color[] cacheSquadPixels; // Method: CacheSquadCustomSprites
        private static Texture2D cacheSquadChangedTexture; // Method: CacheSquadCustomSprites
        private static Vector2Int cacheSquadSpriteSize; // Method: CacheSquadCustomSprites
        private static int cacheSquadSpriteRows; // Method: CacheSquadCustomSprites
        private static int cacheSquadSpriteColumns; // Method: CacheSquadCustomSprites
        private static Sprite cacheSquadRecoloredSprite; // Method: CacheSquadCustomSprites
        private static int cacheSquadI, cacheSquadJ, cacheSquadP, cacheSquadX, cacheSquadY; // Loop variables for `for` loops in CacheSquadCustomSprites

        public static IEnumerator CacheSquadCustomSprites(SavedSquad squad, Dictionary<ConfigData.ShipTypes, List<Sprite>> shipPartSprites, string type, Dictionary<ConfigData.ShipTypes, Vector2Int> sizes, Dialogue dialogue = null)
        {
            // Start the timer to measure how long the sprite processing takes
            cacheSquadStartTime = Time.realtimeSinceStartup;

            if (squad.HasCustomColor)
            {
                Debug.Log($"Saving custom color ({squad.Color}) sprites for {squad.Name}");

                // Getting the list of ships in the squad
                cacheSquadShips = squad.GetSquadShips();
                for (cacheSquadI = 0; cacheSquadI < cacheSquadShips.Count; cacheSquadI++) // Use class-level static `cacheSquadI`
                {
                    cacheSquadShip = cacheSquadShips[cacheSquadI];

                    // If the ship's type exists in the dictionary
                    if (shipPartSprites.ContainsKey(cacheSquadShip.ShipType))
                    {
                        // Get the colors for the current ship type
                        cacheSquadColors = ConfigData.ChangeableShipColors.GetValueOrDefault(cacheSquadShip.ShipType);
                        cacheSquadIndex = 0;

                        // Get the list of sprites for the ship type
                        cacheSquadSprites = shipPartSprites[cacheSquadShip.ShipType];
                        for (cacheSquadJ = 0; cacheSquadJ < cacheSquadSprites.Count; cacheSquadJ++) // Use class-level static `cacheSquadJ`
                        {
                            cacheSquadSprite = cacheSquadSprites[cacheSquadJ];

                            // Check if the ship type requires special handling (e.g., Factory or WarpGate)
                            if (((cacheSquadShip.ShipType == ConfigData.ShipTypes.Factory || cacheSquadShip.ShipType == ConfigData.ShipTypes.WarpGate) && cacheSquadIndex > 0) || type == "remains")
                            {
                                cacheSquadChangeablePixels = SetChangablePixelsForImage(cacheSquadColors, cacheSquadSprite);
                                yield return ConfigData.WaitForEndOfFrame;

                                // Process the sprite
                                cacheSquadSourceTexture = cacheSquadSprite.texture;
                                cacheSquadPixels = cacheSquadSourceTexture.GetPixels();
                                yield return ConfigData.WaitForEndOfFrame;

                                for (cacheSquadP = 0; cacheSquadP < cacheSquadChangeablePixels.Length; cacheSquadP++) // Use class-level static `cacheSquadP`
                                {
                                    squad.Color.a = cacheSquadPixels[cacheSquadChangeablePixels[cacheSquadP]].a; // match the alpha value
                                    cacheSquadPixels[cacheSquadChangeablePixels[cacheSquadP]] = squad.Color;
                                }

                                // Create a new texture for the changed sprite
                                cacheSquadChangedTexture = new Texture2D(cacheSquadSourceTexture.width, cacheSquadSourceTexture.height);
                                yield return ConfigData.WaitForEndOfFrame;
                                cacheSquadChangedTexture.SetPixels(cacheSquadPixels);

                                // Get the sprite's size and calculate rows/columns
                                cacheSquadSpriteSize = sizes[cacheSquadShip.ShipType];
                                cacheSquadSpriteRows = cacheSquadSourceTexture.height / cacheSquadSpriteSize.y;
                                cacheSquadSpriteColumns = cacheSquadSourceTexture.width / cacheSquadSpriteSize.x;

                                // Create new sprites from the modified texture
                                for (cacheSquadY = 0; cacheSquadY < cacheSquadSpriteRows; cacheSquadY++) // Use class-level static `cacheSquadY`
                                {
                                    for (cacheSquadX = 0; cacheSquadX < cacheSquadSpriteColumns; cacheSquadX++) // Use class-level static `cacheSquadX`
                                    {
                                        cacheSquadRecoloredSprite = Sprite.Create(cacheSquadChangedTexture, new Rect(cacheSquadSpriteSize.x * cacheSquadX, (cacheSquadSourceTexture.height - cacheSquadSpriteSize.y * cacheSquadY) - cacheSquadSpriteSize.y, cacheSquadSpriteSize.x, cacheSquadSpriteSize.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
                                        yield return ConfigData.WaitForEndOfFrame;

                                        try
                                        {
                                            cacheSquadShip.GetFleetShip().SaveSpriteToCache(cacheSquadIndex, type, cacheSquadRecoloredSprite.texture.GetPixels(cacheSquadSpriteSize.x * cacheSquadX, (cacheSquadSourceTexture.height - cacheSquadSpriteSize.y * cacheSquadY) - cacheSquadSpriteSize.y, cacheSquadSpriteSize.x, cacheSquadSpriteSize.y), cacheSquadSpriteSize, squad.Color);
                                        }
                                        catch (Exception e)
                                        {
                                            Debug.Log($"Error while trying to save cached sprites: {e}");
                                        }
                                        cacheSquadIndex++;
                                        yield return ConfigData.WaitForEndOfFrame;
                                    }
                                }
                            }
                            else
                            {
                                // Handle regular ship sprite coloring
                                cacheSquadSpriteSize = new Vector2Int(cacheSquadSprite.texture.width, cacheSquadSprite.texture.height);

                                cacheSquadChangeablePixels = Utilities.SetChangablePixelsForImage(cacheSquadColors, cacheSquadSprite);
                                yield return ConfigData.WaitForEndOfFrame;
                                cacheSquadRecoloredSprite = Utilities.SetImageColor(squad.Color, cacheSquadSprite, cacheSquadChangeablePixels);
                                yield return ConfigData.WaitForEndOfFrame;

                                try
                                {
                                    cacheSquadShip.GetFleetShip().SaveSpriteToCache(cacheSquadIndex, "ship", cacheSquadRecoloredSprite.texture.GetPixels(), cacheSquadSpriteSize, squad.Color);
                                }
                                catch (Exception e)
                                {
                                    Debug.Log($"Error while trying to save cached sprites for {cacheSquadShip.GetFleetShip().Name}: {e}");
                                }
                                cacheSquadIndex++;
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

            Debug.Log($"Drawing and saving sprites for {squad.Name} took {(Time.realtimeSinceStartup - cacheSquadStartTime)}s");

            if (dialogue != null)
            {
                dialogue.Hide();
            }
        }

        // Class-level static variables for LoadSquadsFromJson method
        private static List<SavedSquad> savedSquads = new List<SavedSquad>(); // Method: LoadSquadsFromJson
        private static Color color; // Method: LoadSquadsFromJson
        private static SquadStatBlock Stats; // Method: LoadSquadsFromJson
        private static SavedSquad savedSquad; // Method: LoadSquadsFromJson
        private static List<dynamic> ships; // Method: LoadSquadsFromJson
        private static SquadShip squadShip; // Method: LoadSquadsFromJson

        public static List<SavedSquad> LoadSquadsFromJson(List<dynamic> jsonSquads)
        {
            savedSquads.Clear();
            // Iterating through each squad in the jsonSquads
            jsonSquads.ForEach((squad) =>
            {
                // Setting color values based on squad data
                color = new Color((float)squad.Color.r, (float)squad.Color.g, (float)squad.Color.b, (float)squad.Color.a);

                // Creating the Stats block for the squad
                Stats = new SquadStatBlock((string)squad.Stats.Commander, (int)squad.Stats.BattlesFought, (int)squad.Stats.BattlesWon,
                    (int)squad.Stats.ShipsLost, (int)squad.Stats.DamageDone, (int)squad.Stats.DamageReceived, (int)squad.Stats.Kills);

                // Creating the SavedSquad object
                savedSquad = new SavedSquad((int)squad.Id, (int)squad.Side, (string)squad.Name, new Vector2((float)squad.StartingPosition.x, (float)squad.StartingPosition.y),
                    (bool)squad.CeaseFire, (bool)squad.IsMatchingSpeed, ConvertShootingStrategyNameToType[(string)squad.ChosenShootingStrategy], color, Stats);

                // Convert squad's ships data to list of dynamic objects
                ships = squad.Ships.ToObject<List<dynamic>>();

                // Iterate over the ships for the current squad
                ships.ForEach((ship) =>
                {
                    // Adding each ship to the squad
                    squadShip = new SquadShip((int)ship.FleetId, ConvertShipNameToShipType[(string)ship.ShipType], new Vector2((float)ship.Offset.x, (float)ship.Offset.y),
                     savedSquad);

                    savedSquad.AddShipToSquad(squadShip);
                });

                // Add the completed squad to the list of saved squads
                savedSquads.Add(savedSquad);
            });

            // Returning the list of saved squads
            return savedSquads.ToList();
        }



        // Private class-level variables for GetAllKeys method
        private static List<KeyCode> _getAllKeysPressed; // Method: GetAllKeys

        public static List<KeyCode> GetAllKeys()
        {
            _getAllKeysPressed = new List<KeyCode>();
            if (Input.anyKey)
            {
                foreach (KeyCode _getAllKey in Enum.GetValues(typeof(KeyCode))) // Use private static `_getAllKey`
                {
                    if (_getAllKey < KeyCode.Mouse0 && Input.GetKey(_getAllKey))
                    {
                        // Debug.Log("Key pressed: " + _getAllKey);
                        _getAllKeysPressed.Add(_getAllKey);
                        // Debug.Log($"Current keys pressed: {Utilities.ListToString(_newKeyCombination)}");
                    }
                }
            }
            return _getAllKeysPressed;
        }


        // Private class-level variables for SetImageColor method
        private static Texture2D _setImageSourceTexture; // Method: SetImageColor
        private static Color[] _setImagePixels; // Method: SetImageColor
        private static Texture2D _setImageChangedTexture; // Method: SetImageColor
        private static Sprite _setImageRecoloredSprite; // Method: SetImageColor
        private static int _setImagePixelIndex; // Method: SetImageColor
        private static Vector2 _setImageHalf = Vector2.one / 2;

        public static Sprite SetImageColor(Color color, Sprite sprite, int[] changeablePixels)
        {
            _setImageSourceTexture = sprite.texture;
            _setImagePixels = _setImageSourceTexture.GetPixels();

            for (_setImagePixelIndex = 0; _setImagePixelIndex < changeablePixels.Length; _setImagePixelIndex++) // Use private static `_setImagePixelIndex`
            {
                _setImagePixels[changeablePixels[_setImagePixelIndex]] = color;
            }
            _setImageChangedTexture = new Texture2D(_setImageSourceTexture.width, _setImageSourceTexture.height);

            _setImageChangedTexture.SetPixels(_setImagePixels);
            _setImageChangedTexture.Apply(true);

            // Debug.Log($"width: {dimensions.x}, height: {dimensions.y}");
            _setImageRecoloredSprite = Sprite.Create(_setImageChangedTexture, new Rect(0, 0, _setImageSourceTexture.width, _setImageSourceTexture.height), _setImageHalf, ConfigData.PixelsPerUnit);
            return _setImageRecoloredSprite;
        }


        public static Vector2 ForceBounds(float x, float y, float MaxX, float MaxY, float MinX, float MinY)
        {
            return new Vector2(Mathf.Clamp(x, MinX, MaxX), Mathf.Clamp(y, MinY, MaxY));
        }

        // Private class-level variables for RotatePointAroundPoint method
        private static float _rotatePointCosAngle; // Method: RotatePointAroundPoint
        private static float _rotatePointSinAngle; // Method: RotatePointAroundPoint
        private static Vector2 _rotatePointTranslatedVector; // Method: RotatePointAroundPoint
        private static float _rotatePointRotatedX; // Method: RotatePointAroundPoint
        private static float _rotatePointRotatedY; // Method: RotatePointAroundPoint
        private static Vector2 _rotatePointRotatedVector; // Method: RotatePointAroundPoint

        public static Vector2 RotatePointAroundPoint(Vector2 pivot, Vector2 rotatedPoint, float radians)
        {
            _rotatePointCosAngle = Mathf.Cos(radians);
            _rotatePointSinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            _rotatePointTranslatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            _rotatePointRotatedX = _rotatePointTranslatedVector.x * _rotatePointCosAngle - _rotatePointTranslatedVector.y * _rotatePointSinAngle;
            _rotatePointRotatedY = _rotatePointTranslatedVector.x * _rotatePointSinAngle + _rotatePointTranslatedVector.y * _rotatePointCosAngle;

            // Translate the rotated vector back to the original position
            _rotatePointRotatedVector = new Vector2(_rotatePointRotatedX, _rotatePointRotatedY) + pivot;

            return _rotatePointRotatedVector;
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
        // Private class-level variables for RotateIntPointAroundPoint method
        private static double _rotateIntPointCosAngle; // Method: RotateIntPointAroundPoint
        private static double _rotateIntPointSinAngle; // Method: RotateIntPointAroundPoint
        private static Vector2Int _rotateIntPointTranslatedVector; // Method: RotateIntPointAroundPoint
        private static double _rotateIntPointRotatedX; // Method: RotateIntPointAroundPoint
        private static double _rotateIntPointRotatedY; // Method: RotateIntPointAroundPoint
        private static Vector2Int _rotateIntPointRotatedVector; // Method: RotateIntPointAroundPoint

        public static Vector2Int RotateIntPointAroundPoint(Vector2Int pivot, Vector2Int rotatedPoint, float radians)
        {
            _rotateIntPointCosAngle = Mathf.Cos(radians);
            _rotateIntPointSinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            _rotateIntPointTranslatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            _rotateIntPointRotatedX = _rotateIntPointTranslatedVector.x * _rotateIntPointCosAngle - _rotateIntPointTranslatedVector.y * _rotateIntPointSinAngle;
            _rotateIntPointRotatedY = _rotateIntPointTranslatedVector.x * _rotateIntPointSinAngle + _rotateIntPointTranslatedVector.y * _rotateIntPointCosAngle;

            // Translate the rotated vector back to the original position
            _rotateIntPointRotatedVector = new Vector2Int(Convert.ToInt32(_rotateIntPointRotatedX), Convert.ToInt32(_rotateIntPointRotatedY)) + pivot;

            return _rotateIntPointRotatedVector;
        }


        /// <summary>
        /// Rotates the game object on this ship the quickest way towards a rotation and returns true once it reaches that rotation. Returns false once it is done rotating
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <param name="rotationSpeed"></param>
        /// <returns></returns>
        public static bool TimedRotation(Turret turret, float rotation, float rotationSpeed)
        {
            return TimedRotationDifference(turret, rotation, rotationSpeed) == 0;
        }

        // Private class-level variables for TimedRotationDifference method
        private static float _timedRotationDifferenceDifference; // Method: TimedRotationDifference
        private static float _rotationRate;
        private static Vector3 _forward = Vector3.forward;
        private const int _levelOfPrecision = 3;
        private const int _levelOfPrecisionNegative = -_levelOfPrecision;
        //private static Vector3 _timedRotationDifferenceRotationVector; // Method: TimedRotationDifference

        public static float TimedRotationDifference(Ship ship, float rotation, float rotationSpeed)
        {
            _timedRotationDifferenceDifference = Mathf.DeltaAngle(ship.Rotation, rotation);
            _rotationRate = ship.Stage.FixedDeltaTime * rotationSpeed;

            if (_timedRotationDifferenceDifference > _levelOfPrecision)
            {
                //_timedRotationDifferenceRotationVector = new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed);
                //Debug.Log($"For {ship}, the turret has a rotation of {ship.Turrets.First().Rotation}, and a z of {ship.Turrets.First().PieceTransform.eulerAngles.z} before rotating");
                ship.Transform.Rotate(_forward * _rotationRate);
                ship.Rotation += _rotationRate;
                ship.Turrets.ForEach((t) => t.Rotation += _rotationRate);
                //Debug.Log($"For {ship}, Rotation is {ship.Rotation}, z is {ship.Transform.localEulerAngles.z}, a rotation rate of {-_rotationRate} has been applied");
                //Debug.Log($"For {ship}, the turret has a rotation of {ship.Turrets.First().Rotation}, and a z of {ship.Turrets.First().PieceTransform.eulerAngles.z} after rotating");
                return _timedRotationDifferenceDifference;

            }
            else if (_timedRotationDifferenceDifference < _levelOfPrecisionNegative)
            {
                //_timedRotationDifferenceRotationVector = new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed * -1);
                //Debug.Log($"For {ship}, the turret has a rotation of {ship.Turrets.First().Rotation}, and a z of {ship.Turrets.First().PieceTransform.eulerAngles.z} before rotating");
                ship.Transform.Rotate(_forward * -_rotationRate);
                ship.Rotation -= _rotationRate;
                ship.Turrets.ForEach((t) => t.Rotation -= _rotationRate);
                //Debug.Log($"For {ship}, Rotation is {ship.Rotation}, z is {ship.Transform.localEulerAngles.z}, a rotation rate of {-_rotationRate} has been applied");
                //Debug.Log($"For {ship}, the turret has a rotation of {ship.Turrets.First().Rotation}, and a z of {ship.Turrets.First().PieceTransform.eulerAngles.z} after rotating");

                return _timedRotationDifferenceDifference;
            }
            return 0;
            //else
            //{
            //    //entity.Transform.eulerAngles = _forward * rotation;
            //    //entity.Rotation = rotation;
            //    return 0;
            //}
        }
        public static float TimedRotationDifference(Turret turret, float rotation, float rotationSpeed)
        {
            _timedRotationDifferenceDifference = Mathf.DeltaAngle(turret.Rotation, rotation);
            _rotationRate = turret.Stage.FixedDeltaTime * rotationSpeed;

            //Debug.Log($"For turret ship {turret.Ship}, Rotation is {turret.Rotation}, z is {turret.PieceTransform.eulerAngles.z}, a rotation rate of {_rotationRate} is being applied");

            //if (Math.Abs(turret.Rotation - turret.PieceTransform.eulerAngles.z) > 1)
            //{
            //    Debug.LogWarning($"{turret.Ship}, has out of sync turret rotation: Rotation is {turret.Rotation}, z is {turret.PieceTransform.eulerAngles.z}");
            //}

            if (_timedRotationDifferenceDifference > _levelOfPrecision)
            {
                //_timedRotationDifferenceRotationVector = new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed);
                turret.PieceTransform.Rotate(_forward * _rotationRate);
                turret.Rotation += _rotationRate;
                return _timedRotationDifferenceDifference;
            }
            else if (_timedRotationDifferenceDifference < _levelOfPrecisionNegative)
            {
                //_timedRotationDifferenceRotationVector = new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed * -1);
                turret.PieceTransform.Rotate(_forward * -_rotationRate);
                turret.Rotation -= _rotationRate;
                return _timedRotationDifferenceDifference;
            }
            else if (_timedRotationDifferenceDifference != 0)
            {
                //weapon.PieceTransform.Rotate(_forward * _timedRotationDifferenceDifference);
                //weapon.Rotation = _timedRotationDifferenceDifference;
                //Debug.Log(_timedRotationDifferenceDifference);
                turret.PieceTransform.eulerAngles = _forward * rotation;
                turret.Rotation = rotation;
            }
            return 0;

        }

        /// <summary>
        /// Checks if the different in angle between the rotation and the entity is within 3
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        private static float _isRotatedTowardsDifference; // Method: IsRotatedTowards

        public static bool IsRotatedTowards(Turret turret, float rotation)
        {
            _isRotatedTowardsDifference = Mathf.DeltaAngle(turret.Rotation, rotation);

            if (_isRotatedTowardsDifference > _levelOfPrecision || _isRotatedTowardsDifference < _levelOfPrecisionNegative)
            {
                return false;
            }
            return true;
        }
        public static bool IsRotatedTowards(Entity entity, float rotation)
        {
            _isRotatedTowardsDifference = Mathf.DeltaAngle(entity.Rotation, rotation);

            if (_isRotatedTowardsDifference > _levelOfPrecision || _isRotatedTowardsDifference < _levelOfPrecisionNegative)
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
        // Private class-level variables for IsAimedAt method
        private static float _isAimedAtDifference; // Method: IsAimedAt
        private static float _isAimedAtCloseEnough = 3; // Method: IsAimedAt

        public static bool IsAimedAt(Turret turret, float rotation)
        {
            _isAimedAtDifference = Mathf.DeltaAngle(turret.Rotation, rotation);

            if (_isAimedAtDifference > _isAimedAtCloseEnough)
            {
                return false;
            }
            else if (_isAimedAtDifference < (0 - _isAimedAtCloseEnough))
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        // Private class-level variables for ListToString method
        private static string _listToStringResult; // Method: ListToString

        public static string ListToString<T>(List<T> list)
        {
            _listToStringResult = "";
            list.ForEach(_listToStringItem => _listToStringResult += $"{_listToStringItem}, ");

            if (_listToStringResult.Length > 2)
            {
                _listToStringResult = _listToStringResult.Remove(_listToStringResult.Length - 2);
            }
            return _listToStringResult;
        }

        // Private class-level variables for SetUIColor method
        private static Image _setUIColorImage; // Method: SetUIColor
        private static SpriteRenderer _setUIColorSprite; // Method: SetUIColor

        public static void SetUIColor(GameObject gameObject, Color color)
        {
            _setUIColorImage = gameObject.GetComponent<Image>();
            if (_setUIColorImage != null)
            {
                _setUIColorImage.color = color;
            }
            else
            {
                _setUIColorSprite = gameObject.GetComponent<SpriteRenderer>();
                if (_setUIColorSprite != null)
                {
                    _setUIColorSprite.color = color;
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

        public static List<T> JArrayToList<T>(dynamic jArray)
        {
           return ((JArray)jArray).ToList<dynamic>().ConvertAll((item) => (T)item);
        }

        // Private class-level variables for JArrayToWeaponTypes method
        private static List<string> _jArrayToWeaponTypesWeaponList; // Method: JArrayToWeaponTypes

        public static List<ConfigData.WeaponTypes> JArrayToWeaponTypes(dynamic jArray)
        {
            _jArrayToWeaponTypesWeaponList = JArrayToList<string>(jArray);
            return _jArrayToWeaponTypesWeaponList.ConvertAll((_jArrayToWeaponTypesItem) => ConvertWeaponNameToType[_jArrayToWeaponTypesItem]);
        }


        // Private class-level variables for JArrayToProjectileTypes method
        private static List<string> _jArrayToProjectileTypesProjectileList; // Method: JArrayToProjectileTypes

        public static List<ConfigData.ProjectileTypes> JArrayToProjectileTypes(dynamic jArray)
        {
            _jArrayToProjectileTypesProjectileList = JArrayToList<string>(jArray);
            return _jArrayToProjectileTypesProjectileList.ConvertAll((_jArrayToProjectileTypesItem) => ConvertProjectileNameToType[_jArrayToProjectileTypesItem]);
        }

        // Private class-level variables for JArrayToShipTypes method
        private static List<string> _jArrayToShipTypesShipList; // Method: JArrayToShipTypes

        public static List<ConfigData.ShipTypes> JArrayToShipTypes(dynamic jArray)
        {
            _jArrayToShipTypesShipList = JArrayToList<string>(jArray);
            return _jArrayToShipTypesShipList.ConvertAll((_jArrayToShipTypesItem) => ConvertShipNameToShipType[_jArrayToShipTypesItem]);
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
        // Private class-level variables for JArrayToShipTypeDictionary method
        private static Dictionary<ConfigData.ShipTypes, int> _jArrayToShipTypeDictionaryDictionary; // Method: JArrayToShipTypeDictionary
        private static List<dynamic> _jArrayToShipTypeDictionaryList; // Method: JArrayToShipTypeDictionary
        private static Dictionary<string, int> _jArrayToShipTypeDictionaryD; // Method: JArrayToShipTypeDictionary

        public static Dictionary<ConfigData.ShipTypes, int> JArrayToShipTypeDictionary(dynamic jArray)
        {
            _jArrayToShipTypeDictionaryDictionary = new Dictionary<ConfigData.ShipTypes, int>();
            _jArrayToShipTypeDictionaryList = JArrayToList<dynamic>(jArray);
            _jArrayToShipTypeDictionaryList.ForEach((_jArrayToShipTypeDictionaryItem) =>
            {
                _jArrayToShipTypeDictionaryD = ((JObject)_jArrayToShipTypeDictionaryItem).ToObject<Dictionary<string, int>>();
                _jArrayToShipTypeDictionaryDictionary.Add(ConvertShipNameToShipType[_jArrayToShipTypeDictionaryD.Keys.First()], _jArrayToShipTypeDictionaryD.Values.First());
            });
            return _jArrayToShipTypeDictionaryDictionary;
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
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight);
        }
        public static int CalculateMaxTsv(Ship ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight);
        }
        public static int CalculateTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight);
        }
        public static int CalculateMaxTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight);
        }
        // Private class-level variables for CalculateTsv method
        private static double _calculateTsvSpeedValue; // Method: CalculateTsv
        private static int _calculateTsvFullHealthTsv; // Method: CalculateTsv
        private static int _calculateTsvTsv; // Method: CalculateTsv

        public static int CalculateTsv(float speed, float firepower, int health, int sight)
        {
            _calculateTsvSpeedValue = speed / 3;
            _calculateTsvFullHealthTsv = (int)Math.Round((firepower > 0 ? firepower : 1) * (_calculateTsvSpeedValue > 1 ? _calculateTsvSpeedValue : 1) * (Math.Max(health / 200, 1)), 0) + sight;
            _calculateTsvTsv = ((health > 0 ? 1 : 0) * _calculateTsvFullHealthTsv) + ((health > 0 ? 1 : 0) * health);

            return _calculateTsvTsv;
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

        public static long GetNegativeFleetshipId()
        {
            return -(Hash() + ConfigData.CurrentShips.GetFleetShips().Count);
        }
        public static long GetNegativeSavedSquadId()
        {
            return -(Hash() + ConfigData.CurrentShips.GetSavedSquads().Count);
        }




    }
}