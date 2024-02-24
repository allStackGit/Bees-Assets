
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
        public string Type, Description;
        public int Health, Sight, AdditionalTsv;
        public float Speed;
        public List<int> Ranges, Powers;
        public List<float> RatesOfFire, ProjectileValues, RotationRates;
        public List<string> WeaponTypes;

        public ShipStatBlock(string type, string description, int health, List<int> ranges, List<int> powers, int sight, int additionalTsv, 
            List<float> projectileValues, List<float> ratesOfFire, List<float> rotationRates, float speed, List<string> weaponTypes)
        {
            Type = type;
            Description = description;
            Health = health;
            Ranges = ranges;
            Powers = powers;
            Sight = sight;
            AdditionalTsv = additionalTsv;
            ProjectileValues = projectileValues;
            RotationRates = rotationRates;
            RatesOfFire = ratesOfFire;
            Speed = speed;
            WeaponTypes = weaponTypes;

            if (!(Ranges.Count == Powers.Count && Powers.Count == ProjectileValues.Count && ProjectileValues.Count == WeaponTypes.Count && 
                WeaponTypes.Count == RatesOfFire.Count && RatesOfFire.Count == RotationRates.Count))
            {
                Debugger.Exception($"The ship ({Type}) needs to have the same number of Ranges, Powers, ProjectileValues, WeaponTypes, RatesOfFire, and RotationRates stats");
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