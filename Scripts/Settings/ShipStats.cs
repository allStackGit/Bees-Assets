using Assets.Scripts.Scenes;
using Assets.Scripts.Settings;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Assets.Scripts.Settings
{
    public class ShipStats : ServerSettings
    {
        public Dictionary<ConfigData.ShipTypes, ShipStatBlock> ShipStatsList = new Dictionary<ConfigData.ShipTypes, ShipStatBlock>();

        public ShipStats(int userId) : base("ship-stats", userId)
        {
        }
        protected override void ProcessData(string contents)
        {
            //Debug.Log(contents);
            Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(contents)).ForEach((ship) =>
            {

                List<int> range = Utilities.JArrayToList<int>(ship.Range);
                List<int> power = Utilities.JArrayToList<int>(ship.Power);
                List<float> ProjectileValue = Utilities.JArrayToList<float>(ship.ProjectileValue);
                List<float> rateOfFire = Utilities.JArrayToList<float>(ship.RateOfFire);
                List<float> rotationRates = Utilities.JArrayToList<float>(ship.RotationRates);
                List<ConfigData.WeaponTypes> weaponTypes = Utilities.JArrayToWeaponTypes(ship.WeaponTypes);
                List<ConfigData.WeaponSoundTypes> weaponSoundTypes = Utilities.JArrayToWeaponSoundTypes(ship.WeaponSoundTypes);
                List<ConfigData.ProjectileTypes> projectileTypes = Utilities.JArrayToProjectileTypes(ship.ProjectileTypes);

                ShipStatsList.Add(Utilities.ConvertShipNameToShipType[(string) ship.ShipType], new ShipStatBlock(Utilities.ConvertShipNameToShipType[(string) ship.ShipType], (string)ship.Description, (string)ship.CodexDescription, (int)ship.Health,
                    range, power, (int)ship.Sight, (int)ship.Tsv, ProjectileValue,
                    rateOfFire, rotationRates, (float)ship.Speed, weaponTypes, weaponSoundTypes, projectileTypes));
                 
            });
        }
    }
}
