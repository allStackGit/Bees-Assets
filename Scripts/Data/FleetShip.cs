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
        public long Id;
        public int Side;
        public string Name;
        public ConfigData.ShipTypes Type;
        public bool IsVisibleToUser, IsDead, HasCachedSprite;
        public int ShotsFired, DamageDone, DamageReceived, Kills, BattlesFought, BattlesWon, MineralsMined, MineralsMinedThisLevel;
        public int BattlesLost => BattlesFought - BattlesWon;

        public int Health, MaxHealth, Sight;
        public List<int> Range, Power;
        public List<float> RateOfFire, ProjectileValue, RotationRates;
        public float Speed;
        public List<float> SpecialFirePower = new List<float>();
       
        public float Firepower => GetFirepower();

        public FleetShip(long id, string name, ConfigData.ShipTypes type, bool hasCachedSprite, bool isVisibleToUser, bool isDead, int shotsFired, int damageDone, int damageReceived, int kills, int battlesFought, int battlesWon, int mineralsMined)
        {
            Id = id;
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
            Side = Utilities.ConvertShipTypeToSide.GetValueOrDefault(Type);
            GetStats();
        }
        
        public Sprite LoadCachedSprite(int index, string type, Vector2Int size, Color squadColor)
        {
            try
            {
                string path = $"{ConfigData.GetCachePath()}{type}_{Type}_{ColorUtility.ToHtmlStringRGB(squadColor)}_{index}.png";
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(size.x, size.y);
                texture.LoadImage(bytes);
                Debug.Log($"Loaded cached sprites from {path} for Fleetship {Name}");
                return Sprite.Create(texture, new Rect(0, 0, size.x, size.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
            }
            catch (Exception e)
            {
                Debug.Log($"Error while trying to load cached sprites: {e}");
                throw e;
                //return null;
            }
        }

        public void SaveSpriteToCache(int index, string type, Color[] pixels, Vector2Int size, Color squadColor)
        {
            string path = $"{ConfigData.GetCachePath()}{type}_{Type}_{ColorUtility.ToHtmlStringRGB(squadColor)}_{index}.png";
            //Debug.Log($"Saving {path}");
            if (!File.Exists(path))
            {
                Texture2D export = new Texture2D(size.x, size.y);
                export.SetPixels(pixels);
                export.Apply();
                File.WriteAllBytesAsync(path, export.EncodeToPNG());
            }
            HasCachedSprite = true;

        }
        private void GetStats() // [tsv-calculation]
        {
            ShipStatBlock shipInfo = ConfigData.GetShipInfo(Type);
            //Debug.Log($"Got ship info for {Type}. [{shipInfo}]");
            Health = shipInfo.Health;
            MaxHealth = shipInfo.Health;
            Range = shipInfo.Ranges;
            Power = shipInfo.Powers;
            Sight = shipInfo.Sight;
            ProjectileValue = shipInfo.ProjectileValues;
            RotationRates = shipInfo.RotationRates;

            RateOfFire = shipInfo.RatesOfFire;
            Speed = shipInfo.Speed;

            if (Type == ConfigData.ShipTypes.Striker || Type == ConfigData.ShipTypes.Barge)
            {
                SpecialFirePower.Add(shipInfo.Powers.First()/3);
            }
            else if (Type == ConfigData.ShipTypes.YellowJacket)
            {
                SpecialFirePower.Add(shipInfo.Powers.First() / 5);
            }
            else if (Type == ConfigData.ShipTypes.FireBarge)
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
        //public int GetTsv() { 
        //    return Utilities.CalculateTsv(this);
        //}
        public int GetTsv() 
        {
            return Utilities.CalculateMaxTsv(this.Type);
        }
        public int GetCapacity()
        {
            //Debug.Log($"Calculating TSV for {Type}. Firepower: {Firepower}");
            return GetTsv();
        }
        public bool Equals(FleetShip ship)
        {
            return ship.Id == Id;
        }
        public string ToJson()
        {
            return $"{{\"i\": {Id}, \"n\": \"{Name}\", \"t\": {(int) Type}, \"v\": {(IsVisibleToUser ? 1 : 0)}, \"d\": {(IsDead ? 1 : 0)}," +
                $"\"s\": {(HasCachedSprite ? 1 : 0)}, \"f\": {ShotsFired}, \"dd\": {DamageDone}, \"r\": {DamageReceived}, \"k\": {Kills}, \"b\": {BattlesFought}, \"w\": {BattlesWon}, " +
                $"\"m\": {MineralsMined}}}";
        }
          
    }
}