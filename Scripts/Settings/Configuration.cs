using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
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
        public int StorageChunkSize;
        public int StandardMaxTimeOnQueue;
        public float TimeScale;
        public float AISquadPatrolTime;
        public int AIPatrolMaxSize;
        public int AIRandomMovementMaxDistance = 256;
        public float AISquadGuardTime;
        public float AISquadFollowingTime;
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

            return ConfigData.StandardMaxTimeOnQueue;
        }

        public Configuration(ulong userId) : base("configuration", userId)
        {
        }

        protected override void ProcessData(string contents)
        {
            JObject settings = JObject.Parse(contents);

            IsDeadVersion = settings.Value<bool>("IsDeadVersion");
            UseLocalStorage = settings.Value<bool>("UseLocalStorage");
            MirrorStorage = settings.Value<bool>("MirrorStorage");
            MaxSquadSize = settings.Value<int>("MaxSquadSize");
            MaxSquadWidth = settings.Value<int>("MaxSquadWidth");
            MaxSquadHeight = settings.Value<int>("MaxSquadHeight");
            RotationMultiplier = settings.Value<int>("RotationMultiplier");
            HumanSide = settings.Value<int>("HumanSide");
            BeeSide = settings.Value<int>("BeeSide");
            DoesUserHaveController = settings.Value<bool>("DoesUserHaveController");
            StorageChunkSize = settings.Value<int>("StorageChunkSize");
            StandardMaxTimeOnQueue = settings.Value<int>("StandardMaxTimeOnQueue");
            TimeScale = settings.Value<int>("TimeScale");
            AISquadPatrolTime = settings.Value<int>("AISquadPatrolTime");
            AIPatrolMaxSize = settings.Value<int>("AIPatrolMaxSize");
            AIRandomMovementMaxDistance = settings.Value<int>("AIRandomMovementMaxDistance");
            AISquadGuardTime = settings.Value<int>("AISquadGuardTime");
            AISquadFollowingTime = settings.Value<int>("AISquadFollowingTime");
            CarrierCarryDroneMax = settings.Value<int>("CarrierCarryDroneMax");
            CarrierCarryStrikerMax = settings.Value<int>("CarrierCarryStrikerMax");
            CarrierSquadCount = settings.Value<int>("CarrierSquadCount");

            TotalLevels = CampaignMissionCatalog.Definitions.Count;

            Yes = settings.Value<string>("Yes");
            No = settings.Value<string>("No");
            OK = settings.Value<string>("OK");
            AreYouSure = settings.Value<string>("AreYouSure");
            AreYouSureExit = settings.Value<string>("AreYouSureExit");
            LevelProgressLost = settings.Value<string>("LevelProgressLost");
            DeleteSquadConfirmation = settings.Value<string>("DeleteSquadConfirmation");
            ClearSquadConfirmation = settings.Value<string>("ClearSquadConfirmation");
            LoadSquadConfirmation = settings.Value<string>("LoadSquadConfirmation");
            ChooseSquadConfirmation = settings.Value<string>("ChooseSquadConfirmation");
            UnchooseSquadConfirmation = settings.Value<string>("UnchooseSquadConfirmation");
            GoBackConfirmation = settings.Value<string>("GoBackConfirmation");
            OverCapacityAlertTitle = settings.Value<string>("OverCapacityAlertTitle");
            NoChosenSquadsAlertTitle = settings.Value<string>("NoChosenSquadsAlertTitle");
            ChoosingUnsavedSquadAlertTitle = settings.Value<string>("ChoosingUnsavedSquadAlertTitle");
            ChoosingDeadSquadAlertTitle = settings.Value<string>("ChoosingDeadSquadAlertTitle");
            SquadSavingStatusAlertTitle = settings.Value<string>("SquadSavingStatusAlertTitle");
            CannotDuplicateSquadAlertTitle = "Cannot Duplicate Squad";
            OverCapacityAlert = settings.Value<string>("OverCapacityAlert");
            NoChosenSquadsAlert = settings.Value<string>("NoChosenSquadsAlert");
            ChoosingUnsavedSquadAlert = settings.Value<string>("ChoosingUnsavedSquadAlert");
            ChoosingDeadSquadAlert = settings.Value<string>("ChoosingDeadSquadAlert");
            SquadSavingStatusAlert = settings.Value<string>("SquadSavingStatusAlert");
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

            AISide = settings.Value<string>("AISide") == "BeeSide" ? BeeSide : HumanSide;
            UserSide = settings.Value<string>("UserSide") == "BeeSide" ? BeeSide : HumanSide;
            SquadMakerFirstSide = settings.Value<string>("SquadMakerFirstSide") == "BeeSide" ? BeeSide : HumanSide;
            SquadMakerSecondSide = settings.Value<string>("SquadMakerSecondSide") == "BeeSide" ? BeeSide : HumanSide;

            CensoredWords = new HashSet<string>(settings["CensoredWords"].ToObject<List<string>>());
            Tooltips = ParseStringDictionary(settings["Tooltips"] as JArray);
            ConfigData.ShipTurningRadius = (360.0f / RotationMultiplier) / (2 * Mathf.PI);
        }

        private static Dictionary<string, string> ParseStringDictionary(JArray entries)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (entries == null)
            {
                return result;
            }

            foreach (JObject entry in entries.Children<JObject>())
            {
                foreach (JProperty property in entry.Properties())
                {
                    result.Add(property.Name, property.Value.Value<string>());
                }
            }
            return result;
        }
    }
}
