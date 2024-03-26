
using Assets.Scripts.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;

namespace Assets.Scripts.Settings
{
    public class Configuration : ServerSettings
    {
        public bool IsDeadVersion;
        public bool UseLocalStorage;
        public bool MirrorStorage;
        public List<string> CensoredWords;
        public int MaxSquadSize; 
        public int MaxSquadWidth;
        public int MaxSquadHeight;
        public int RotationMultiplier;

        public int HumanSide;
        public int BeeSide;

        public bool DoesUserHaveController;
        public int StorageChunkSize; // how many commands to complete before storing them in the server
        public int StandardMaxTimeOnQueue; // the default max amount of time (in ms) for a server request to wait on the queue before resending
        public float TimeScale;
        public float AISquadPatrolTime; // how many seconds a patrol should last for the AI
        public int AIPatrolMaxSize;
        public int AIRandomMovementMaxDistance;
        public float AISquadGuardTime; // how many seconds a guard command should last for the AI
        public float AISquadFollowingTime; // how many seconds a squad should follow the closest friendly squad
        public int CarrierCarryDroneMax;
        public int CarrierCarryStrikerMax;
        public int CarrierSquadCount;
        public List<string> ShootingStrategies;
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
        public string OverCapacityAlert;
        public string NoChosenSquadsAlert;
        public string ChoosingUnsavedSquadAlert;
        public string ChoosingDeadSquadAlert;


        // [alert] Should probably be based off of user progress at some point

        public List<string> VisibleBeeShipTypes;
        public List<string> VisibleHumanShipTypes;
        public List<string> InvisibleBeeShipTypes;
        public List<string> InvisibleHumanShipTypes;
        public List<string> AllShipTypes;
        public int AISide; // [alert]  // depends on whether the user is playing as the humans or the bees
        public int UserSide;
        public int SquadMakerFirstSide;
        public int SquadMakerSecondSide;
        public int TotalLevels; // [alert] should be set to actual number of levels
        public int SquadGenerationCount;


        public bool MirrorLocalStorageToServer => UseLocalStorage && MirrorStorage;
        public bool MirrorServerStorageToLocal => UseLocalStorage && MirrorStorage;

        public Configuration(int userId) : base("configuration", userId)
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
            ConfigData.StandardMaxTimeOnQueue = StandardMaxTimeOnQueue;
            TimeScale = (int)so.TimeScale;
            AISquadPatrolTime = (int)so.AISquadPatrolTime;
            AIPatrolMaxSize = (int)so.AIPatrolMaxSize;
            AIRandomMovementMaxDistance = (int)so.AIRandomMovementMaxDistance;
            AISquadGuardTime = (int)so.AISquadGuardTime;
            AISquadFollowingTime = (int)so.AISquadFollowingTime;
            CarrierCarryDroneMax = (int)so.CarrierCarryDroneMax;
            CarrierCarryStrikerMax = (int)so.CarrierCarryStrikerMax;
            CarrierSquadCount = (int)so.CarrierSquadCount;
            TotalLevels = (int)so.TotalLevels;
            SquadGenerationCount = (int)so.SquadGenerationCount;

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
            OverCapacityAlert = (string)so.OverCapacityAlert;
            NoChosenSquadsAlert = (string)so.NoChosenSquadsAlert;
            ChoosingUnsavedSquadAlert = (string)so.ChoosingUnsavedSquadAlert;
            ChoosingDeadSquadAlert = (string)so.ChoosingDeadSquadAlert;

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

            CensoredWords = Utilities.JArrayToList<string>(so.CensoredWords);
            ShootingStrategies = Utilities.JArrayToList<string>(so.ShootingStrategies);
            VisibleBeeShipTypes = Utilities.JArrayToList<string>(so.VisibleBeeShipTypes);
            VisibleHumanShipTypes = Utilities.JArrayToList<string>(so.VisibleHumanShipTypes);
            InvisibleBeeShipTypes = Utilities.JArrayToList<string>(so.InvisibleBeeShipTypes);
            InvisibleHumanShipTypes = Utilities.JArrayToList<string>(so.InvisibleHumanShipTypes);
            Tooltips = Utilities.JArrayToDictionary<string, string>(so.Tooltips);


            AllShipTypes = InvisibleBeeShipTypes;
            AllShipTypes.AddRange(InvisibleHumanShipTypes);
            AllShipTypes.AddRange(VisibleBeeShipTypes);
            AllShipTypes.AddRange(VisibleHumanShipTypes);
        }
    }
}