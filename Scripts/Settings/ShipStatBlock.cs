
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Settings
{
    public class ShipStatBlock
    {
        /*
         Container for setting stats for a ship
         */
        public string Description, CodexDescription;
        public ConfigData.ShipTypes Type;
        public int Health, Sight, Tsv;
        public float Speed;
        public List<int> Ranges, Powers;
        public List<float> RatesOfFire, ProjectileValues, RotationRates;
        public List<ConfigData.WeaponTypes> WeaponTypes;
        public List<ConfigData.ProjectileTypes> ProjectileTypes;

        public ShipStatBlock(ConfigData.ShipTypes type, string description, string codexDescription, int health, List<int> ranges, List<int> powers, int sight, int tsv, 
            List<float> projectileValues, List<float> ratesOfFire, List<float> rotationRates, float speed, List<ConfigData.WeaponTypes> weaponTypes, List<ConfigData.ProjectileTypes> projectileTypes)
        {
            Type = type;
            Description = description;
            CodexDescription = codexDescription;
            Health = health;
            Ranges = ranges;
            Powers = powers;
            Sight = sight;
            Tsv = tsv;
            ProjectileValues = projectileValues;
            RotationRates = rotationRates;
            RatesOfFire = ratesOfFire;
            Speed = speed;
            WeaponTypes = weaponTypes;
            ProjectileTypes = projectileTypes;

            if (!(Ranges.Count == Powers.Count && Powers.Count == ProjectileValues.Count && ProjectileValues.Count == WeaponTypes.Count && 
                WeaponTypes.Count == RatesOfFire.Count && RatesOfFire.Count == RotationRates.Count && RotationRates.Count == ProjectileTypes.Count))
            {
                Debug.LogError($"The ship ({Type}) needs to have the same number of Ranges, Powers, ProjectileValues, WeaponTypes, RatesOfFire, ProjectileTypes, and RotationRates stats");
            }
        }
        public string PrintRange()
        {
            string str = Utilities.ListToString(Ranges);
            return str.Length > 0 ? str : "N/A";
        }
        public string PrintPower()
        {
            string str = Utilities.ListToString(Powers);
            return str.Length > 0 ? str : "N/A";
        }
        public string PrintRateOfFire()
        {
            string str = "";
            RatesOfFire.ForEach(r => str += $"{r}{(r > 0 ? "s" : "")}, " );
            if (str.Length > 0)
            {
                str = str.Remove(str.Length - 2);
            }
            else
            {
                str = "N/A";
            }
            return str;
        }
       
    }
}