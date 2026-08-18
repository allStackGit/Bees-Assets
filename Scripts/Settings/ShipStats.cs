using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace Assets.Scripts.Settings
{
    public class ShipStats : ServerSettings
    {
        public Dictionary<ConfigData.ShipTypes, ShipStatBlock> ShipStatsList = new Dictionary<ConfigData.ShipTypes, ShipStatBlock>();

        public ShipStats(ulong userId) : base("ship-stats", userId)
        {
        }

        protected override void ProcessData(string contents)
        {
            JArray ships = JArray.Parse(contents);
            foreach (JObject ship in ships.Children<JObject>())
            {
                ConfigData.ShipTypes shipType = Utilities.ConvertShipNameToShipType[ship.Value<string>("ShipType")];
                List<int> range = ship["Range"].ToObject<List<int>>();
                List<int> power = ship["Power"].ToObject<List<int>>();
                List<float> projectileValue = ship["ProjectileValue"].ToObject<List<float>>();
                List<float> rateOfFire = ship["RateOfFire"].ToObject<List<float>>();
                List<float> rotationRates = ship["RotationRates"].ToObject<List<float>>();

                List<string> weaponTypeNames = ship["WeaponTypes"].ToObject<List<string>>();
                List<ConfigData.WeaponTypes> weaponTypes = weaponTypeNames.ConvertAll(name => Utilities.ConvertWeaponNameToType[name]);
                List<string> weaponSoundTypeNames = ship["WeaponSoundTypes"].ToObject<List<string>>();
                List<ConfigData.WeaponSoundTypes> weaponSoundTypes = weaponSoundTypeNames.ConvertAll(name => Utilities.ConvertWeaponSoundNameToType[name]);
                List<string> projectileTypeNames = ship["ProjectileTypes"].ToObject<List<string>>();
                List<ConfigData.ProjectileTypes> projectileTypes = projectileTypeNames.ConvertAll(name => Utilities.ConvertProjectileNameToType[name]);

                ShipStatsList.Add(shipType, new ShipStatBlock(
                    shipType,
                    ship.Value<string>("Description"),
                    ship.Value<string>("CodexDescription"),
                    ship.Value<int>("Health"),
                    range,
                    power,
                    ship.Value<int>("Sight"),
                    ship.Value<int>("Tsv"),
                    projectileValue,
                    rateOfFire,
                    rotationRates,
                    ship.Value<float>("Speed"),
                    weaponTypes,
                    weaponSoundTypes,
                    projectileTypes));
            }
        }
    }
}
