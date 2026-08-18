using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Assets.Scripts.Settings
{
    public class StartingSettings : ServerSettings
    {
        public Dictionary<ConfigData.ShipTypes, int> HumanStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> BeeStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> HumanCampaignStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> BeeCampaignStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> HumanChallengeStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> BeeChallengeStartingShips;
        public List<int> SupplyCapacity;
        public List<ConfigData.ShipTypes> HumanShipTypes => HumanStartingShips.Keys.ToList();
        public List<ConfigData.ShipTypes> BeeShipTypes => BeeStartingShips.Keys.ToList();

        public StartingSettings(ulong userId) : base("starting-settings", userId)
        {
        }

        protected override void ProcessData(string contents)
        {
            JObject settings = JObject.Parse(contents);

            SupplyCapacity = settings["SupplyCapacity"].ToObject<List<int>>();
            HumanStartingShips = ParseShipCounts(settings["HumanStartingShips"] as JArray);
            BeeStartingShips = ParseShipCounts(settings["BeeStartingShips"] as JArray);
            HumanCampaignStartingShips = ParseShipCounts(settings["HumanCampaignStartingShips"] as JArray);
            BeeCampaignStartingShips = ParseShipCounts(settings["BeeCampaignStartingShips"] as JArray);
            HumanChallengeStartingShips = ParseShipCounts(settings["HumanChallengeStartingShips"] as JArray);
            BeeChallengeStartingShips = ParseShipCounts(settings["BeeChallengeStartingShips"] as JArray);
        }

        private static Dictionary<ConfigData.ShipTypes, int> ParseShipCounts(JArray entries)
        {
            Dictionary<ConfigData.ShipTypes, int> result = new Dictionary<ConfigData.ShipTypes, int>();
            if (entries == null)
            {
                return result;
            }

            foreach (JObject entry in entries.Children<JObject>())
            {
                foreach (JProperty property in entry.Properties())
                {
                    result.Add(
                        Utilities.ConvertShipNameToShipType[property.Name],
                        property.Value.Value<int>());
                }
            }
            return result;
        }
    }
}
