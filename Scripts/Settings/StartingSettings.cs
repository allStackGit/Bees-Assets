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
        public List<int> SupplyCapacity ; 
        public string DefaultShootingStrategy;
        public List<string> HumanShipTypes => HumanStartingShips.Keys.ToList(); // Barge, Carrier, Cruiser, Dreadnought, Drone, Factory, Fire Ship, Frigate, Gunship, Scout, Striker, Warp Gate
        public List<string> BeeShipTypes => BeeStartingShips.Keys.ToList(); // Beehive, Bumblebee, Carpenter Bee, Honeybee, Hornet, Leafcutter, Queen, Wasp, Yellow Jacket

        public StartingSettings(int userId, Scene scene) : base("starting-settings", userId, scene)
        {
        }
        protected override void ProcessData(string contents)
        {
            dynamic so = JsonConvert.DeserializeObject(contents);

            DefaultShootingStrategy = (string) so.DefaultShootingStrategy;
            SupplyCapacity = Utilities.JArrayToList<int>(so.SupplyCapacity);
            
            HumanStartingShips = Utilities.JArrayToDictionary<string, int>(so.HumanStartingShips);
            BeeStartingShips = Utilities.JArrayToDictionary<string, int>(so.BeeStartingShips);
        }
    }
}