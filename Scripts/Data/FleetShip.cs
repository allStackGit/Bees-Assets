using Assets.Scripts.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class FleetShip 
    {
        public int Id, Side;
        public string Name, Type;
        public bool IsVisibleToUser, IsDead, HasCachedSprite;
        public int ShotsFired, DamageDone, DamageReceived, Kills, BattlesFought, BattlesWon, MineralsMined, MineralsMinedThisLevel;
        public int BattlesLost => BattlesFought - BattlesWon;

        public int Health, MaxHealth, AdditionalTsv, Sight;
        public List<int> Range, Power;
        public List<float> RateOfFire, ProjectileValue, RotationRates;
        public float Speed;
        public List<float> SpecialFirePower = new List<float>();
       
        public float Firepower => GetFirepower();

        public FleetShip(int id, int side, string name, string type, bool hasCachedSprite, bool isVisibleToUser, bool isDead, int shotsFired, int damageDone, int damageReceived, int kills, int battlesFought, int battlesWon, int mineralsMined)
        {
            Id = id;
            Side = side;
            Name = name;
            Type = type;
            HasCachedSprite = hasCachedSprite;
            IsVisibleToUser = isVisibleToUser;
            IsDead = isDead;
            ShotsFired = shotsFired;
            DamageDone = damageDone;
            DamageReceived = damageReceived;
            Kills = kills;
            BattlesFought = battlesFought;
            BattlesWon = battlesWon;
            MineralsMined = mineralsMined;

            GetStats();
        }
        
        public Sprite LoadCachedSprite(int index, Vector2Int size)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes($"{ConfigData.GetCachePath()}/{Name}_{index}.png");
                Texture2D texture = new Texture2D(size.x, size.y);
                //texture.filterMode = FilterMode.Trilinear;
                texture.LoadImage(bytes);
                return Sprite.Create(texture, new Rect(0, 0, size.x, size.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
            }
            catch (Exception e)
            {
                //Debug.Log($"Error while trying to load cached sprites: {e}");
                return null;
            }
        }

        public void SaveSpriteToCache(int index, Color[] pixels, Vector2Int size)
        {
            string path = $"{ConfigData.GetCachePath()}/{Name}_{index}.png";
            Texture2D export = new Texture2D(size.x, size.y);
            export.SetPixels(pixels);
            export.Apply();
            File.WriteAllBytesAsync(path, export.EncodeToPNG());
        }
        private void GetStats() // [tsv-calculation]
        {
            ShipStatBlock shipInfo = ConfigData.GetShipInfo(Type);
            //Debug.Log($"Got ship info for {Type}. [{shipInfo}]");
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
                //Debug.Log($"AdditionalTSV for Carrier is {AdditionalTsv}. {drone.GetTsv()} for each drone and {striker.GetTsv()} for each striker");
            }
            else if (Type == "Striker" || Type == "Barge")
            {
                SpecialFirePower.Add(shipInfo.Powers.First()/3);
            }
            else if (Type == "Yellow Jacket")
            {
                SpecialFirePower.Add(shipInfo.Powers.First() / 5);
            }
            else if (Type == "Fire Ship")
            {
                SpecialFirePower.Add((shipInfo.Powers.First() * shipInfo.ProjectileValues.First()));
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
                //Debug.Log($"Firepower calc for {Type}: range: {range}, power: {power*projectileValue}, rateofFire: {rateOfFire}");
                
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
            //Debug.Log($"Calculating TSV for {Type}. Firepower: {Firepower}");
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