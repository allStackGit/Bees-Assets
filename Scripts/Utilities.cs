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
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Unity.Mathematics;

using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using static UnityEngine.GraphicsBuffer;
using static UnityEngine.RuleTile.TilingRuleOutput;
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
            {ConfigData.ShipTypeLetters.X, ConfigData.ShipTypes.HumanTarget },
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
            {ConfigData.ShipTypeLetters.X, 'X' },
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
            {ConfigData.ShipTypes.HumanTarget, 'X' },
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
            {ConfigData.ShipTypes.HumanTarget, ConfigData.ShipTypeLetters.X },
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
            {ConfigData.ShipTypes.HumanTarget, 2 }
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
            {ConfigData.ProjectileTypes.FireTankExplosion, 2 },
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
            {ConfigData.ShipTypes.HumanTarget, "Human Target"},
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
            {"Human Target", ConfigData.ShipTypes.HumanTarget},
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
            {"Human Targets", ConfigData.ShipTypes.HumanTarget},
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
            {ConfigData.ShipTypes.HumanTarget, "Human Targets"},
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
            {"Move To Point", ConfigData.CommandTypes.MoveToPoint },
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
            {ConfigData.CommandTypes.MoveToPoint, "Move To Point" },
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
            { ConfigData.MatchupStrategyTypes.TypeX, ConfigData.ShipTypes.HumanTarget },

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
            { "Type X", ConfigData.MatchupStrategyTypes.TypeX },


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
            { ConfigData.ShootingStrategyTypes.TypeX, ConfigData.ShipTypes.HumanTarget },

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
            { "Type W", ConfigData.ShootingStrategyTypes.TypeW},
            { "Type X", ConfigData.ShootingStrategyTypes.TypeX},
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
            { ConfigData.ShootingStrategyTypes.TypeW, "Type W" },
            { ConfigData.ShootingStrategyTypes.TypeX, "Type X" },
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

        public static Dictionary<string, ConfigData.WeaponSoundTypes> ConvertWeaponSoundNameToType = new Dictionary<string, ConfigData.WeaponSoundTypes>
        {
            { "None", ConfigData.WeaponSoundTypes.None },
            { "Small Laser", ConfigData.WeaponSoundTypes.SmallLaser },
            { "Big Laser", ConfigData.WeaponSoundTypes.BigLaser },
            { "Flagship Charging Laser", ConfigData.WeaponSoundTypes.FlagshipChargingLaser },
            { "Flagship Laser", ConfigData.WeaponSoundTypes.FlagshipLaser },
            { "Queen Laser", ConfigData.WeaponSoundTypes.QueenLaser },
            { "Beam Cannon", ConfigData.WeaponSoundTypes.BeamCannon },
            { "Bowtie Laser", ConfigData.WeaponSoundTypes.BowtieLaser },
            { "Light Cannon", ConfigData.WeaponSoundTypes.LightCannon },
            { "Rocket Launch", ConfigData.WeaponSoundTypes.RocketLaunch },
            { "Bomb", ConfigData.WeaponSoundTypes.Bomb },
            { "Fire Barge Bomb", ConfigData.WeaponSoundTypes.FireBargeBomb },
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
            { "Fire Tank Explosion", ConfigData.ProjectileTypes.FireTankExplosion },
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


        private static Random _rnd = new Random();

        public static IDisposable UseDeterministicRandom(int seed)
        {
            Random previous = _rnd;
            _rnd = new Random(seed);
            return new RandomScope(previous);
        }

        private sealed class RandomScope : IDisposable
        {
            private Random _previous;

            public RandomScope(Random previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_previous == null)
                {
                    return;
                }

                _rnd = _previous;
                _previous = null;
            }
        }
        private static long _uniqueHash_tempHash;

        public static long Hash()
        {
            return Unique53Hash();
        }

        public static long UniqueHash()
        {
            _uniqueHash_tempHash = RandomLong(10000000);
            while (ConfigData.UsedHashes.Contains(_uniqueHash_tempHash))
            {
                Debug.LogWarning($"A duplicate hash was found! {_uniqueHash_tempHash} There are {ConfigData.UsedHashes.Count} unique hashes stored");
                _uniqueHash_tempHash = RandomLong(10000000);
            }
            ConfigData.UsedHashes.Add(_uniqueHash_tempHash);
            return _uniqueHash_tempHash;
        }

        private static readonly long _clientId = GenerateClientId();
        private static long _counter = 0;

        private static long GenerateClientId()
        {
            Span<byte> buf = stackalloc byte[4];
            RandomNumberGenerator.Fill(buf);
            int raw = BitConverter.ToInt32(buf);
            return (uint)raw & ((1 << 21) - 1);
        }

        private static uint _ctr;
        private static long _id;
        public static long Unique53Hash()
        {
            _ctr = (uint)Interlocked.Increment(ref _counter);
            _id = (_clientId << 32) | _ctr;
            return _id;
        }

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

        private static ScaledTimer _shakeTimer = new ScaledTimer();
        private static ScaledTimer _cancelTimer = new ScaledTimer();
        public static void ShakeObject(GameObject gameObject, Vector2 originalPosition)
        {
            gameObject.transform.localPosition = originalPosition + new Vector2(RandomFloat(.5f) * RandomSign(), RandomFloat(.5f) * RandomSign());
            gameObject.transform.localEulerAngles = new Vector3(0, 0, RandomFloat(15f) * RandomSign());
        }

        public static void Shake(Level level, GameObject gameObject, float duration, Action endAction)
        {
            Vector2 originalPosition = gameObject.transform.localPosition;
            _shakeTimer.Reuse(.025f, () =>
            {
                ShakeObject(gameObject, originalPosition);
            }, true);
            level.AddTimer(_shakeTimer);
            _cancelTimer.Reuse(duration, () =>
            {
                CancelShake(level);
                endAction();
            }, false);
            level.AddTimer(_cancelTimer);
        }

        public static void CancelShake(Level level)
        {
            level.CancelTimer(_shakeTimer);
        }

        public static bool AreVectorsEqual(Vector2 a, Vector2 b)
        {
            return Math.Floor(a.x) == Math.Floor(b.x) && Math.Floor(a.y) == Math.Floor(b.y);
        }
        public static Random GetRandom()
        {
            return _rnd;
        }
        public static int RandomInt(int max = int.MaxValue) 
        {
            return _rnd.Next(max);
        }
        private static readonly string _letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public static char RandomLetter()
        {
            return _letters[RandomInt(_letters.Length)];
        }
        private static string hexidecimal = "abcdef0123456789";
        public static string hexidecimalString()
        {
            string str = "";
            for (int i = 0; i < 16; i++)
            {
                str += hexidecimal[RandomInt(hexidecimal.Length)];
            }
            return str;
        }
        public static long RandomLong(int max = int.MaxValue)
        {
            return (long) _rnd.Next(max) * _rnd.Next(max);
        }
        public static float RandomFloat(float max)
        {
            return (float) _rnd.NextDouble() * max;
        }
        public static int RandomSign()
        {
            return RandomInt(2) > 0 ? 1 : -1;
        }
        public static bool CoinToss()
        {
            return RandomInt(2) == 0;
        }
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

        private static Vector2 worldUnitsToScreenPixels_baseWorldPoint;
        private static Vector2 worldUnitsToScreenPixels_screenPoint;
        private static Vector2 screenPixelsToWorldUnits_baseWorldPoint;
        private static Vector2 screenPixelsToWorldUnits_worldPoint;
        private static string writeJsonFile_path;
        private static string writeTextFile_path;

        public static Vector2 WorldUnitsToScreenPixels(Vector2 vector, Camera camera)
        {
            worldUnitsToScreenPixels_baseWorldPoint = camera.WorldToScreenPoint(Vector2.zero);
            worldUnitsToScreenPixels_screenPoint = camera.WorldToScreenPoint(vector);
            return new Vector2(Mathf.Abs(worldUnitsToScreenPixels_baseWorldPoint.x - worldUnitsToScreenPixels_screenPoint.x),
                               Mathf.Abs(worldUnitsToScreenPixels_baseWorldPoint.y - worldUnitsToScreenPixels_screenPoint.y));
        }

        public static Vector2 ScreenPixelsToWorldUnits(Vector2 vector, Camera camera)
        {
            screenPixelsToWorldUnits_baseWorldPoint = camera.ScreenToWorldPoint(Vector2.zero);
            screenPixelsToWorldUnits_worldPoint = camera.ScreenToWorldPoint(vector);
            return new Vector2(Mathf.Abs(screenPixelsToWorldUnits_baseWorldPoint.x - screenPixelsToWorldUnits_worldPoint.x),
                               Mathf.Abs(screenPixelsToWorldUnits_baseWorldPoint.y - screenPixelsToWorldUnits_worldPoint.y));
        }

        public static void WriteJsonFile(string contents)
        {
            writeJsonFile_path = $"{ConfigData.GetBasePath()}/{Hash()}.json";
            File.WriteAllText(writeJsonFile_path, contents);
        }

        public static void WriteTextFile(string contents)
        {
            writeTextFile_path = $"{ConfigData.GetBasePath()}/{Hash()}.txt";
            File.WriteAllText(writeTextFile_path, contents);
        }

        private static Texture2D sourceTexture_SetChangablePixelsForImage;
        private static Color[] pixels_SetChangablePixelsForImage;
        private static List<int> indexes_SetChangablePixelsForImage;
        private static float threshhold;
        private static float threshholdSquared;
        private static int c;
        private static int _i;
        private static float colorR;
        private static float colorG;
        private static float colorB;
        private static float deltaR;
        private static float deltaG;
        private static float deltaB;
        private static float distanceSquared;

        public static int[] GetChangablePixelsForImage(Color[] colors, Sprite sprite)
        {
            sourceTexture_SetChangablePixelsForImage = sprite.texture;
            pixels_SetChangablePixelsForImage = sourceTexture_SetChangablePixelsForImage.GetPixels();
            indexes_SetChangablePixelsForImage = new List<int>();
            threshhold = .035f;
            threshholdSquared = threshhold * threshhold;

            for (c = 0; c < colors.Length; c++)
            {
                colorR = colors[c].r;
                colorG = colors[c].g;
                colorB = colors[c].b;
                for (_i = 0; _i < pixels_SetChangablePixelsForImage.Length; _i++)
                {
                    deltaR = pixels_SetChangablePixelsForImage[_i].r - colorR;
                    deltaG = pixels_SetChangablePixelsForImage[_i].g - colorG;
                    deltaB = pixels_SetChangablePixelsForImage[_i].b - colorB;
                    distanceSquared = (deltaR * deltaR) + (deltaG * deltaG) + (deltaB * deltaB);
                    if (distanceSquared < threshholdSquared && pixels_SetChangablePixelsForImage[_i].a > 0)
                    {
                        indexes_SetChangablePixelsForImage.Add(_i);
                    }
                }
            }

            return indexes_SetChangablePixelsForImage.ToArray();
        }

        public static IEnumerator CacheSquadCustomSprites(SavedSquad squad, Dictionary<ConfigData.ShipTypes, List<Sprite>> shipPartSprites, string type, Dictionary<ConfigData.ShipTypes, Vector2Int> sizes, Dialogue dialogue = null)
        {
            float cacheSquadStartTime = Time.realtimeSinceStartup;

            if (squad.HasCustomColor)
            {
                Debug.Log($"Saving custom color ({squad.Color}) {type} sprites for {squad.Name}");
                List<SquadShip> cacheSquadShips = squad.GetSquadShips().ToList();
                for (int i = 0; i < cacheSquadShips.Count; i++) 
                {
                    SquadShip cacheSquadShip = cacheSquadShips[i];
                    if (cacheSquadShip.ShipType == ConfigData.ShipTypes.Carrier)
                    {
                        cacheSquadShips.Add(new SquadShip(-1, ConfigData.ShipTypes.Drone, Vector2.zero));
                        cacheSquadShips.Add(new SquadShip(-1, ConfigData.ShipTypes.Striker, Vector2.zero));
                    }

                    if (shipPartSprites.ContainsKey(cacheSquadShip.ShipType))
                    {
                        Color[]  cacheSquadColors = ConfigData.ChangeableShipColors.GetValueOrDefault(cacheSquadShip.ShipType);
                        int cacheSquadIndex = 0;
                        List<Sprite> cacheSquadSprites = shipPartSprites[cacheSquadShip.ShipType];
                        for (int j = 0; j < cacheSquadSprites.Count; j++)
                        {
                            Sprite cacheSquadSprite = cacheSquadSprites[j];
                            if (((cacheSquadShip.ShipType == ConfigData.ShipTypes.Factory || cacheSquadShip.ShipType == ConfigData.ShipTypes.WarpGate) && cacheSquadIndex > 0) || type == "remains")
                            {
                                int[] cacheSquadChangeablePixels = GetChangablePixelsForImage(cacheSquadColors, cacheSquadSprite);
                                yield return ConfigData.WaitForEndOfFrame;
                                Texture2D cacheSquadSourceTexture = cacheSquadSprite.texture;
                                Color[]  cacheSquadPixels = cacheSquadSourceTexture.GetPixels();
                                yield return ConfigData.WaitForEndOfFrame;

                                for (int p = 0; p < cacheSquadChangeablePixels.Length; p++) 
                                {
                                    Color cacheSquadColor = squad.Color;
                                    cacheSquadColor.a = cacheSquadPixels[cacheSquadChangeablePixels[p]].a;
                                    cacheSquadPixels[cacheSquadChangeablePixels[p]] = cacheSquadColor;
                                }

                                Texture2D cacheSquadChangedTexture = new Texture2D(cacheSquadSourceTexture.width, cacheSquadSourceTexture.height);
                                cacheSquadChangedTexture.filterMode = FilterMode.Point;
                                yield return ConfigData.WaitForEndOfFrame;
                                cacheSquadChangedTexture.SetPixels(cacheSquadPixels);

                                Vector2Int cacheSquadSpriteSize = sizes[cacheSquadShip.ShipType];
                                int cacheSquadSpriteRows = cacheSquadSourceTexture.height / cacheSquadSpriteSize.y;
                                int cacheSquadSpriteColumns = cacheSquadSourceTexture.width / cacheSquadSpriteSize.x;

                                for (int y = 0; y < cacheSquadSpriteRows; y++)
                                {
                                    for (int x = 0; x < cacheSquadSpriteColumns; x++)
                                    {
                                        Sprite cacheSquadRecoloredSprite = Sprite.Create(cacheSquadChangedTexture, new Rect(cacheSquadSpriteSize.x * x, (cacheSquadSourceTexture.height - cacheSquadSpriteSize.y * y) - cacheSquadSpriteSize.y, cacheSquadSpriteSize.x, cacheSquadSpriteSize.y), ConfigData.HalfSize);
                                        yield return ConfigData.WaitForEndOfFrame;

                                        try
                                        {
                                            cacheSquadShip.GetFleetShip().SaveSpriteToCache(cacheSquadIndex, type, cacheSquadRecoloredSprite.texture.GetPixels(cacheSquadSpriteSize.x * x, (cacheSquadSourceTexture.height - cacheSquadSpriteSize.y * y) - cacheSquadSpriteSize.y, cacheSquadSpriteSize.x, cacheSquadSpriteSize.y), cacheSquadSpriteSize, squad.Color);
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
                                Vector2Int cacheSquadSpriteSize = sizes[cacheSquadShip.ShipType];
                                if (j > 0)
                                {
                                    cacheSquadSpriteSize = new Vector2Int(cacheSquadSprite.texture.width, cacheSquadSprite.texture.height);
                                }

                                int[] cacheSquadChangeablePixels = GetChangablePixelsForImage(cacheSquadColors, cacheSquadSprite);
                                yield return ConfigData.WaitForEndOfFrame;
                                Sprite cacheSquadRecoloredSprite = SetImageColor(squad.Color, cacheSquadSprite, cacheSquadChangeablePixels);
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

        private static Texture2D _setImageSourceTexture;
        private static Color[] _setImagePixels;
        private static Texture2D _setImageChangedTexture;
        private static Sprite _setImageRecoloredSprite;
        private static int _setImagePixelIndex;
        private static Vector2 _setImageHalf = Vector2.one / 2;

        public static Sprite SetImageColor(Color color, Sprite sprite, int[] changeablePixels)
        {
            _setImageSourceTexture = sprite.texture;
            _setImagePixels = _setImageSourceTexture.GetPixels();

            for (_setImagePixelIndex = 0; _setImagePixelIndex < changeablePixels.Length; _setImagePixelIndex++)
            {
                _setImagePixels[changeablePixels[_setImagePixelIndex]] = color;
            }
            _setImageChangedTexture = new Texture2D(_setImageSourceTexture.width, _setImageSourceTexture.height);
            _setImageChangedTexture.filterMode = FilterMode.Point;

            _setImageChangedTexture.SetPixels(_setImagePixels);
            _setImageChangedTexture.Apply(true);
            _setImageRecoloredSprite = Sprite.Create(_setImageChangedTexture, new Rect(0, 0, _setImageSourceTexture.width, _setImageSourceTexture.height), _setImageHalf, ConfigData.PixelsPerUnit);
            return _setImageRecoloredSprite;
        }

        private static List<SavedSquad> savedSquads = new List<SavedSquad>();
        private static Color color;
        private static SquadStatBlock Stats;
        private static SavedSquad savedSquad;
        private static List<dynamic> ships;
        private static SquadShip squadShip;

        public static List<SavedSquad> LoadSquadsFromJson(List<dynamic> jsonSquads)
        {
            savedSquads.Clear();
            jsonSquads.ForEach((squad) =>
            {
                color = new Color((float)squad.Color.r, (float)squad.Color.g, (float)squad.Color.b, (float)squad.Color.a);
                Stats = new SquadStatBlock((string)squad.Stats.Commander, (int)squad.Stats.BattlesFought, (int)squad.Stats.BattlesWon,
                    (int)squad.Stats.ShipsLost, (int)squad.Stats.DamageDone, (int)squad.Stats.DamageReceived, (int)squad.Stats.Kills);
                savedSquad = new SavedSquad((long)squad.Id, (int)squad.Side, (string)squad.Name, new Vector2((float)squad.StartingPosition.x, (float)squad.StartingPosition.y),
                    (bool)squad.CeaseFire, (bool)squad.IsMatchingSpeed, ConvertShootingStrategyNameToType[(string)squad.ChosenShootingStrategy], color, Stats);
                ships = squad.Ships.ToObject<List<dynamic>>();
                ships.ForEach((ship) =>
                {
                    squadShip = new SquadShip((long)ship.FleetId, ConvertShipNameToShipType[(string)ship.ShipType], new Vector2((float)ship.Offset.x, (float)ship.Offset.y));
                    savedSquad.AddShipToSquad(squadShip);
                });
                savedSquads.Add(savedSquad);
            });
            return savedSquads.ToList();
        }

        private static List<(Vector2, Vector2)> _obstacles = new List<(Vector2, Vector2)>();
        private static (Vector2, Vector2) _obstacle;
        public static List<(Vector2, Vector2)> LoadObstaclesFromJson(List<dynamic> jsonObstacles)
        {
            _obstacles.Clear();
            jsonObstacles.ForEach((obstacle) =>
            {
                _obstacle = (new Vector2((float) obstacle.Position.x, (float) obstacle.Position.y), new Vector2((float)obstacle.Scale.x, (float)obstacle.Scale.y));
                _obstacles.Add(_obstacle);
            });
            return _obstacles.ToList();
        }

        private static List<KeyCode> _getAllKeysPressed;

        public static List<KeyCode> GetAllKeys()
        {
            _getAllKeysPressed = new List<KeyCode>();
            if (Input.anyKey)
            {
                foreach (KeyCode _getAllKey in Enum.GetValues(typeof(KeyCode)))
                {
                    if (_getAllKey < KeyCode.Mouse0 && Input.GetKey(_getAllKey))
                    {
                        _getAllKeysPressed.Add(_getAllKey);
                    }
                }
            }
            return _getAllKeysPressed;
        }

        public static Vector2 ForceBounds(float x, float y, float MaxX, float MaxY, float MinX, float MinY)
        {
            return new Vector2(Mathf.Clamp(x, MinX, MaxX), Mathf.Clamp(y, MinY, MaxY));
        }

        private static float _rotatePointCosAngle;
        private static float _rotatePointSinAngle;
        private static Vector2 _rotatePointTranslatedVector;
        private static float _rotatePointRotatedX;
        private static float _rotatePointRotatedY;
        private static Vector2 _rotatePointRotatedVector;

        public static Vector2 RotatePointAroundPoint(Vector2 pivot, Vector2 rotatedPoint, float radians)
        {
            _rotatePointCosAngle = Mathf.Cos(radians);
            _rotatePointSinAngle = Mathf.Sin(radians);
            _rotatePointTranslatedVector = rotatedPoint - pivot;
            _rotatePointRotatedX = _rotatePointTranslatedVector.x * _rotatePointCosAngle - _rotatePointTranslatedVector.y * _rotatePointSinAngle;
            _rotatePointRotatedY = _rotatePointTranslatedVector.x * _rotatePointSinAngle + _rotatePointTranslatedVector.y * _rotatePointCosAngle;
            _rotatePointRotatedVector = new Vector2(_rotatePointRotatedX, _rotatePointRotatedY) + pivot;
            return _rotatePointRotatedVector;
        }

        public static Vector2 CirclePoint(float angle, float distance, Vector2 position)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            return new Vector2((position.x + (Mathf.Cos(angle) * distance)), (position.y + (Mathf.Sin(angle) * distance)));
        }

        private static double _rotateIntPointCosAngle;
        private static double _rotateIntPointSinAngle;
        private static Vector2Int _rotateIntPointTranslatedVector;
        private static double _rotateIntPointRotatedX;
        private static double _rotateIntPointRotatedY;
        private static Vector2Int _rotateIntPointRotatedVector;

        public static Vector2Int RotateIntPointAroundPoint(Vector2Int pivot, Vector2Int rotatedPoint, float radians)
        {
            _rotateIntPointCosAngle = Mathf.Cos(radians);
            _rotateIntPointSinAngle = Mathf.Sin(radians);
            _rotateIntPointTranslatedVector = rotatedPoint - pivot;
            _rotateIntPointRotatedX = _rotateIntPointTranslatedVector.x * _rotateIntPointCosAngle - _rotateIntPointTranslatedVector.y * _rotateIntPointSinAngle;
            _rotateIntPointRotatedY = _rotateIntPointTranslatedVector.x * _rotateIntPointSinAngle + _rotateIntPointTranslatedVector.y * _rotateIntPointCosAngle;
            _rotateIntPointRotatedVector = new Vector2Int(Convert.ToInt32(_rotateIntPointRotatedX), Convert.ToInt32(_rotateIntPointRotatedY)) + pivot;
            return _rotateIntPointRotatedVector;
        }

        public static bool TimedRotation(Turret turret, float rotation, float rotationSpeed)
        {
            return TimedRotationDifference(turret, rotation, rotationSpeed) == 0;
        }

        private static float _timedRotationDifferenceDifference;
        private static float _rotationRate;
        private static Vector3 _forward = Vector3.forward;
        private const int _levelOfPrecision = 3;
        private const int _levelOfPrecisionNegative = -_levelOfPrecision;

        public static float TimedRotationDifference(Ship ship, float rotation, float rotationSpeed)
        {
            _timedRotationDifferenceDifference = Mathf.DeltaAngle(ship.Rotation, rotation);
            _rotationRate = ship.Stage.FixedDeltaTime * rotationSpeed;

            if (_timedRotationDifferenceDifference > _levelOfPrecision)
            {
                ship.Transform.Rotate(_forward * _rotationRate);
                ship.Rotation += _rotationRate;
                ship.Turrets.ForEach((t) => t.Rotation += _rotationRate);
                return _timedRotationDifferenceDifference;
            }
            else if (_timedRotationDifferenceDifference < _levelOfPrecisionNegative)
            {
                ship.Transform.Rotate(_forward * -_rotationRate);
                ship.Rotation -= _rotationRate;
                ship.Turrets.ForEach((t) => t.Rotation -= _rotationRate);
                return _timedRotationDifferenceDifference;
            }
            return 0;
        }
        public static float TimedRotationDifference(Turret turret, float rotation, float rotationSpeed)
        {
            _timedRotationDifferenceDifference = Mathf.DeltaAngle(turret.Rotation, rotation);
            _rotationRate = turret.Stage.FixedDeltaTime * rotationSpeed;

            if (_timedRotationDifferenceDifference > _levelOfPrecision)
            {
                turret.PieceTransform.Rotate(_forward * _rotationRate);
                turret.Rotation += _rotationRate;
                return _timedRotationDifferenceDifference;
            }
            else if (_timedRotationDifferenceDifference < _levelOfPrecisionNegative)
            {
                turret.PieceTransform.Rotate(_forward * -_rotationRate);
                turret.Rotation -= _rotationRate;
                return _timedRotationDifferenceDifference;
            }
            else if (_timedRotationDifferenceDifference != 0)
            {
                turret.PieceTransform.eulerAngles = _forward * rotation;
                turret.Rotation = rotation;
            }
            return 0;
        }

        private static float _isRotatedTowardsDifference;

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

        private static float _isAimedAtDifference;
        private static float _isAimedAtCloseEnough = 3;

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

        private static string _listToStringResult;

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

        private static Image _setUIColorImage;
        private static SpriteRenderer _setUIColorSprite;

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
                    Debug.LogWarning($"Tried to set the color of {gameObject.name} which doesn't have a UI image.");
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
        }

        public static List<T> JArrayToList<T>(dynamic jArray)
        {
           return ((JArray)jArray).ToList<dynamic>().ConvertAll((item) => (T)item);
        }

        private static List<string> _jArrayToWeaponTypesWeaponList;

        public static List<ConfigData.WeaponTypes> JArrayToWeaponTypes(dynamic jArray)
        {
            _jArrayToWeaponTypesWeaponList = JArrayToList<string>(jArray);
            return _jArrayToWeaponTypesWeaponList.ConvertAll((_jArrayToWeaponTypesItem) => ConvertWeaponNameToType[_jArrayToWeaponTypesItem]);
        }

        private static List<string> _jArrayToWeaponSoundTypesWeaponList;
        public static List<ConfigData.WeaponSoundTypes> JArrayToWeaponSoundTypes(dynamic jArray)
        {
            _jArrayToWeaponSoundTypesWeaponList = JArrayToList<string>(jArray);
            return _jArrayToWeaponSoundTypesWeaponList.ConvertAll((_jArrayToWeaponTypesItem) => ConvertWeaponSoundNameToType[_jArrayToWeaponTypesItem]);
        }

        private static List<string> _jArrayToProjectileTypesProjectileList;

        public static List<ConfigData.ProjectileTypes> JArrayToProjectileTypes(dynamic jArray)
        {
            _jArrayToProjectileTypesProjectileList = JArrayToList<string>(jArray);
            return _jArrayToProjectileTypesProjectileList.ConvertAll((_jArrayToProjectileTypesItem) => ConvertProjectileNameToType[_jArrayToProjectileTypesItem]);
        }

        private static List<string> _jArrayToShipTypesShipList;

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
        private static Dictionary<ConfigData.ShipTypes, int> _jArrayToShipTypeDictionaryDictionary;
        private static List<dynamic> _jArrayToShipTypeDictionaryList;
        private static Dictionary<string, int> _jArrayToShipTypeDictionaryD;

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

        public static int CalculateTsv(Ship ship)
        {
            return (int) (GetMaxTsv(ship.ShipType) * CalculateHealthFactor(ship)) + (ship.Health > 0 ? ship.FleetShip.MineralsMinedThisLevel : 0);
        }
        public static float CalculateHealthFactor(Ship ship)
        {
            return ship.Health > 0 ? (float)(ship.MaxHealth + ship.Health) / (ship.MaxHealth * 2) : 0;
        }
       
        public static int GetMaxTsv(ConfigData.ShipTypes type)
        {
            return ConfigData.GetShipInfo(type).Tsv;
        }

        public static float CalculateFirepower(int power, int range, float rateOfFire, float rotationRate, float ProjectileValue, float specialFirepower)
        {
            return rateOfFire > 0 ? ((((power*ProjectileValue) / rateOfFire) * Mathf.Clamp(rotationRate/128, .5f, 1.25f)) * Mathf.Pow((range / 20), 2)) : specialFirepower;
        }

        public static bool HasObstaclesInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstaclesLayerMask).collider != null;
        }
        public static Collider2D GetObstaclesInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstaclesLayerMask).collider;
        }

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
            Texture2D texture = new Texture2D(array.Length, array[0].Length, TextureFormat.RGB24, false);
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
