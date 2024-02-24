using Assets.Scripts.Settings;

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class FleetShip 
    {
        public int Id, Side;
        public string Name, Type;
        public bool IsVisibleToUser, IsDead;
        public int ShotsFired, DamageDone, DamageReceived, Kills, BattlesFought, BattlesWon;
        public int BattlesLost => BattlesFought - BattlesWon;

        public int Health, MaxHealth, AdditionalTsv, Sight;
        public List<int> Range, Power;
        public List<float> RateOfFire, ProjectileValue, RotationRates;
        public float Speed;
        public List<float> SpecialFirePower = new List<float>();
       
        public float Firepower => GetFirepower();

        public FleetShip(int id, int side, string name, string type, bool isVisibleToUser, bool isDead, int shotsFired, int damageDone, int damageReceived, int kills, int battlesFought, int battlesWon)
        {
            Id = id;
            Side = side;
            Name = name;
            Type = type;
            IsVisibleToUser = isVisibleToUser;
            IsDead = isDead;
            ShotsFired = shotsFired;
            DamageDone = damageDone;
            DamageReceived = damageReceived;
            Kills = kills;
            BattlesFought = battlesFought;
            BattlesWon = battlesWon;
            GetStats();
        }
        private void GetStats()
        {
            ShipStatBlock shipInfo = ConfigData.GetShipInfo(Type);
            //Debugger.Log($"Got ship info for {Type}. [{shipInfo}]");
            Health = shipInfo.Health;
            MaxHealth = shipInfo.Health;
            Range = shipInfo.Ranges;
            Power = shipInfo.Powers;
            AdditionalTsv = shipInfo.AdditionalTsv;
            Sight = shipInfo.Sight;
            ProjectileValue = shipInfo.ProjectileValues;
            RotationRates = shipInfo.RotationRates;

            RateOfFire = shipInfo.RatesOfFire;
            Speed = shipInfo.Speed;

            if (Type == "Carrier")
            {
                AdditionalTsv = Utilities.CalculateCarrierAdditionalTsv();
                //Debugger.Log($"AdditionalTSV for Carrier is {AdditionalTsv}. {drone.GetTsv()} for each drone and {striker.GetTsv()} for each striker");
            }
            else if (Type == "Striker")
            {
                SpecialFirePower.Add(shipInfo.Powers.First()/5);
            }
            else if (Type == "Yellow Jacket")
            {
                SpecialFirePower.Add(shipInfo.Powers.First() / 10);
            }
            else if (Type == "Fire Ship")
            {
                SpecialFirePower.Add((shipInfo.Powers.First() * shipInfo.ProjectileValues.First()));
            }
            else
            {
                ProjectileValue.ForEach(x =>
                {
                    SpecialFirePower.Add(0);
                });
            }

        }
        public bool IsShipVisibleAndAlive()
        {
            return !IsDead && IsVisibleToUser;
        }
        public float GetFirepower()
        {
            float sum = 0;
            for (int i = 0; i < ProjectileValue.Count; i++)
            {
                int range = Range.ElementAt(i);
                int power = Power.ElementAt(i);
                float rateOfFire = RateOfFire.ElementAt(i);
                float projectileValue = ProjectileValue.ElementAt(i);
                float specialFirePower = SpecialFirePower.ElementAt(i);
                float rotationRate = RotationRates.ElementAt(i);
                //Debugger.Log($"Firepower calc for {Type}: range: {range}, power: {power*projectileValue}, rateofFire: {rateOfFire}");
                
                sum += Utilities.CalculateFirepower(power, range, rateOfFire, rotationRate, projectileValue, specialFirePower);
            }
            return sum;
        }
        public int GetTsv() { 
            return Utilities.CalculateTsv(this);
        }
        public int GetMaxTsv() 
        {
            return Utilities.CalculateMaxTsv(this);
        }
        public int GetCapacity()
        {
            return GetTsv();
        }
        public int GetMaxCapacity()
        {
            //Debugger.Log($"Calculating TSV for {Type}. Firepower: {Firepower}");
            return GetMaxTsv();
        }
        public bool Equals(FleetShip ship)
        {
            return ship.Id == Id;
        }
        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
          
    }
}