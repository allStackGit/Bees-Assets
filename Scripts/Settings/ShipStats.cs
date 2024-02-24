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
        public Dictionary<string, ShipStatBlock> ShipStatsList = new Dictionary<string, ShipStatBlock>();

        public ShipStats(int userId, Scene scene) : base("ship-stats", userId, scene)
        {
        }
        protected override void ProcessData(string contents)
        {

            Utilities.JArrayToDynamicList((JArray)JsonConvert.DeserializeObject(contents)).ForEach((ship) =>
            {

                List<int> range = Utilities.JArrayToList<int>(ship.Range);
                List<int> power = Utilities.JArrayToList<int>(ship.Power);
                List<float> ProjectileValue = Utilities.JArrayToList<float>(ship.ProjectileValue);
                List<float> rateOfFire = Utilities.JArrayToList<float>(ship.RateOfFire);
                List<float> rotationRates = Utilities.JArrayToList<float>(ship.RotationRates);
                List<string> types = Utilities.JArrayToList<string>(ship.WeaponTypes);

                ShipStatsList.Add((string)ship.ShipType, new ShipStatBlock((string)ship.ShipType, (string)ship.Description, (int)ship.Health,
                    range, power, (int)ship.Sight, (int)ship.AdditionalTsv, ProjectileValue,
                    rateOfFire, rotationRates, (float)ship.Speed, types));
                 
            });
        }
    }
}
