
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Settings
{
    public class Configuration : ServerSettings
    {
        public bool IsDeadVersion;
        public bool UseLocalStorage;
        public bool MirrorStorage;
        public HashSet<string> CensoredWords;
        public int MaxSquadSize; 
        public int MaxSquadWidth;
        public int MaxSquadHeight;
        public int RotationMultiplier;

        public int HumanSide;
        public int BeeSide;

        public bool DoesUserHaveController;
        public int StorageChunkSize; // how many commands to complete before storing them in the server
        public int StandardMaxTimeOnQueue; // default maximum time in seconds before a server request is eligible for resend
        public float TimeScale;
        public float AISquadPatrolTime; // how many seconds a patrol should last for the AI
        public int AIPatrolMaxSize;
        public int AIRandomMovementMaxDistance = 256;
        public float AISquadGuardTime; // how many seconds a guard command should last for the AI
        public float AISquadFollowingTime; // how many seconds a squad should follow the closest friendly squad
        public int CarrierCarryDroneMax;
        public int CarrierCarryStrikerMax;
        public int CarrierSquadCount;
        public int TotalLevels;
        public Dictionary<string, string> Tooltips;

        public string Yes;
        public string No;
        public string OK;
        public string AreYouSure;
        public string AreYouSureExit;
        public string LevelProgressLost;
        public string DeleteSquadConfirmation;
        public string ClearSquadConfirmation;
        public string LoadSquadConfirmation;
        public string ChooseSquadConfirmation;
        public string UnchooseSquadConfirmation;
        public string GoBackConfirmation;
        public string OverCapacityAlertTitle;
        public string NoChosenSquadsAlertTitle;
        public string ChoosingUnsavedSquadAlertTitle;
        public string ChoosingDeadSquadAlertTitle;
        public string SquadSavingStatusAlertTitle;
        public string CannotDuplicateSquadAlertTitle;
        public string OverCapacityAlert;
        public string NoChosenSquadsAlert;
        public string ChoosingUnsavedSquadAlert;
        public string ChoosingDeadSquadAlert;
        public string SquadSavingStatusAlert;
        public string CannotDuplicateSquadAlert;


        public int AISide;
        public int UserSide;
        public int SquadMakerFirstSide;
        public int SquadMakerSecondSide;


        public bool MirrorLocalStorageToServer => UseLocalStorage && MirrorStorage;
        public bool MirrorServerStorageToLocal => !UseLocalStorage && MirrorStorage;

        public static int GetStandardMaxTimeOnQueue()
        {
            Configuration configuration = ConfigData.Configuration;
            if (configuration != null && configuration.IsLoaded && configuration.StandardMaxTimeOnQueue > 0)
            {
                return configuration.StandardMaxTimeOnQueue;
            }

            // Configuration itself must be fetched before the server-owned value is known.
            return ConfigData.StandardMaxTimeOnQueue;
        }

        public Configuration(ulong userId) : base("configuration", userId)
        {
        }
        protected override void ProcessData(string contents)
        {
            dynamic so = JsonConvert.DeserializeObject(contents);

            IsDeadVersion = (bool)so.IsDeadVersion;
            UseLocalStorage = (bool)so.UseLocalStorage;
            MirrorStorage = (bool)so.MirrorStorage;
            MaxSquadSize = (int)so.MaxSquadSize;
            MaxSquadWidth = (int)so.MaxSquadWidth;
            MaxSquadHeight = (int)so.MaxSquadHeight;
            RotationMultiplier = (int)so.RotationMultiplier;
            HumanSide = (int)so.HumanSide;
            BeeSide = (int)so.BeeSide;
            DoesUserHaveController = (bool)so.DoesUserHaveController;
            StorageChunkSize = (int)so.StorageChunkSize;
            StandardMaxTimeOnQueue = (int)so.StandardMaxTimeOnQueue;
            TimeScale = (int)so.TimeScale;
            AISquadPatrolTime = (int)so.AISquadPatrolTime;
            AIPatrolMaxSize = (int)so.AIPatrolMaxSize;
            AIRandomMovementMaxDistance = (int)so.AIRandomMovementMaxDistance;
            AISquadGuardTime = (int)so.AISquadGuardTime;
            AISquadFollowingTime = (int)so.AISquadFollowingTime;
            CarrierCarryDroneMax = (int)so.CarrierCarryDroneMax;
            CarrierCarryStrikerMax = (int)so.CarrierCarryStrikerMax;
            CarrierSquadCount = (int)so.CarrierSquadCount;

            // The version-5 database dump advertises TotalLevels=10 while the same database
            // contains campaign IDs 0-11. Runtime completion must follow the missions this client
            // actually knows how to configure, not stale/inconsistent server metadata.
            TotalLevels = CampaignMissionCatalog.Definitions.Count;

            Yes = (string)so.Yes;
            No = (string)so.No;
            OK = (string)so.OK;
            AreYouSure = (string)so.AreYouSure;
            AreYouSureExit = (string)so.AreYouSureExit;
            LevelProgressLost = (string)so.LevelProgressLost;
            DeleteSquadConfirmation = (string)so.DeleteSquadConfirmation;
            ClearSquadConfirmation = (string)so.ClearSquadConfirmation;
            LoadSquadConfirmation = (string)so.LoadSquadConfirmation;
            ChooseSquadConfirmation = (string)so.ChooseSquadConfirmation;
            UnchooseSquadConfirmation = (string)so.UnchooseSquadConfirmation;
            GoBackConfirmation = (string)so.GoBackConfirmation;
            OverCapacityAlertTitle = (string)so.OverCapacityAlertTitle;
            NoChosenSquadsAlertTitle = (string)so.NoChosenSquadsAlertTitle;
            ChoosingUnsavedSquadAlertTitle = (string)so.ChoosingUnsavedSquadAlertTitle;
            ChoosingDeadSquadAlertTitle = (string)so.ChoosingDeadSquadAlertTitle;
            SquadSavingStatusAlertTitle = (string)so.SquadSavingStatusAlertTitle;
            CannotDuplicateSquadAlertTitle = "Cannot Duplicate Squad";
            OverCapacityAlert = (string)so.OverCapacityAlert;
            NoChosenSquadsAlert = (string)so.NoChosenSquadsAlert;
            ChoosingUnsavedSquadAlert = (string)so.ChoosingUnsavedSquadAlert;
            ChoosingDeadSquadAlert = (string)so.ChoosingDeadSquadAlert;
            SquadSavingStatusAlert = (string)so.SquadSavingStatusAlert;
            CannotDuplicateSquadAlert = "There are not enough ships in the fleet to duplicate this squad";


            if (MaxSquadWidth == -1)
            {
                MaxSquadWidth = MaxSquadSize;
            }

            if (AISquadGuardTime == -1)
            {
                AISquadGuardTime = AISquadPatrolTime;
            }

            if (AISquadFollowingTime == -1)
            {
                AISquadFollowingTime = AISquadPatrolTime;
            }

            if ((string)so.AISide == "BeeSide")
            {
                AISide = BeeSide;
            }
            else
            {
                AISide = HumanSide;
            }

            if ((string) so.UserSide == "BeeSide")
            {
                UserSide = BeeSide;
            }
            else
            {
                UserSide = HumanSide;
            }

            if ((string)so.SquadMakerFirstSide == "BeeSide")
            {
                SquadMakerFirstSide = BeeSide;
            }
            else
            {
                SquadMakerFirstSide = HumanSide;
            }

            if ((string)so.SquadMakerSecondSide == "BeeSide")
            {
                SquadMakerSecondSide = BeeSide;
            }
            else
            {
                SquadMakerSecondSide = HumanSide;
            }

            CensoredWords = new HashSet<string>(Utilities.JArrayToList<string>(so.CensoredWords));
            Tooltips = Utilities.JArrayToDictionary<string, string>(so.Tooltips);
            ConfigData.ShipTurningRadius = (360.0f / RotationMultiplier) / (2 * Mathf.PI);

        }
    }
}