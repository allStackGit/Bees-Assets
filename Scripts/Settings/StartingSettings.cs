using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Assets.Scripts.Scenes;

namespace Assets.Scripts.Settings
{
    public class StartingSettings : ServerSettings
    {
        public Dictionary<ConfigData.ShipTypes, int> HumanStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> BeeStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> HumanCampaignStartingShips;
        public Dictionary<ConfigData.ShipTypes, int> BeeCampaignStartingShips;
        public List<int> SupplyCapacity; 
        public string DefaultShootingStrategy;
        public List<ConfigData.ShipTypes> HumanShipTypes => HumanStartingShips.Keys.ToList(); // Barge, Carrier, Cruiser, Dreadnought, Drone, Factory, Fire Barge, Frigate, Gunship, Scout, Striker, Warp Gate
        public List<ConfigData.ShipTypes> BeeShipTypes => BeeStartingShips.Keys.ToList(); // Beehive, Bumblebee, Carpenter Bee, Honeybee, Hornet, Leafcutter, Queen, Wasp, Yellow Jacket

        public StartingSettings(int userId) : base("starting-settings", userId)
        {
        }
        protected override void ProcessData(string contents)
        {
            dynamic so = JsonConvert.DeserializeObject(contents);

            DefaultShootingStrategy = (string) so.DefaultShootingStrategy;
            SupplyCapacity = Utilities.JArrayToList<int>(so.SupplyCapacity);
            
            HumanStartingShips = Utilities.JArrayToDictionary<ConfigData.ShipTypes, int>(so.HumanStartingShips);
            BeeStartingShips = Utilities.JArrayToDictionary<ConfigData.ShipTypes, int>(so.BeeStartingShips);

            HumanCampaignStartingShips = Utilities.JArrayToDictionary<ConfigData.ShipTypes, int>(so.HumanCampaignStartingShips);
            BeeCampaignStartingShips = Utilities.JArrayToDictionary<ConfigData.ShipTypes, int>(so.BeeCampaignStartingShips);
        }
    }
}