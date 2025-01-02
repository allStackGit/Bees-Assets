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
        public Dictionary<string, int> HumanStartingShips;
        public Dictionary<string, int> BeeStartingShips;
        public Dictionary<string, int> HumanCampaignStartingShips;
        public Dictionary<string, int> BeeCampaignStartingShips;
        public List<int> SupplyCapacity ; 
        public string DefaultShootingStrategy;
        public List<string> HumanShipTypes => HumanStartingShips.Keys.ToList(); // Barge, Carrier, Cruiser, Dreadnought, Drone, Factory, Fire Ship, Frigate, Gunship, Scout, Striker, Warp Gate
        public List<string> BeeShipTypes => BeeStartingShips.Keys.ToList(); // Beehive, Bumblebee, Carpenter Bee, Honeybee, Hornet, Leafcutter, Queen, Wasp, Yellow Jacket

        public StartingSettings(int userId) : base("starting-settings", userId)
        {
        }
        protected override void ProcessData(string contents)
        {
            dynamic so = JsonConvert.DeserializeObject(contents);

            DefaultShootingStrategy = (string) so.DefaultShootingStrategy;
            SupplyCapacity = Utilities.JArrayToList<int>(so.SupplyCapacity);
            
            HumanStartingShips = Utilities.JArrayToDictionary<string, int>(so.HumanStartingShips);
            BeeStartingShips = Utilities.JArrayToDictionary<string, int>(so.BeeStartingShips);

            HumanCampaignStartingShips = Utilities.JArrayToDictionary<string, int>(so.HumanCampaignStartingShips);
            BeeCampaignStartingShips = Utilities.JArrayToDictionary<string, int>(so.BeeCampaignStartingShips);
        }
    }
}