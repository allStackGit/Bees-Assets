using Assets.Scripts.Entities;
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

        // Not saved to JSON
        public bool DoesBelongToSavedSquad;
        public bool IsLoadedIntoLevel;
       
        public float Firepower => GetFirepower();

        public FleetShip(long id, ConfigData.ShipTypes type, bool hasCachedSprite, bool isDead, int shotsFired, int damageDone, int damageReceived, int kills, int battlesFought, int battlesWon, int mineralsMined, string name = "")
        {
            Id = id;
            Type = type;
            Name = name == "" ? $"{GenerateShipName()}" : name;
            HasCachedSprite = hasCachedSprite;
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
        public string GenerateShipName()
        {
            return Utilities.ConvertShipTypeToSide[Type] == ConfigData.Configuration.UserSide ? $"{Utilities.ConvertShipTypeToName[Type]} {Utilities.ConvertShipTypeToShipTypeLetter[Type]}-{Utilities.RandomInt(100)}" : Utilities.hexidecimalString();
        }
        public Sprite LoadCachedSprite(int index, string type, Vector2Int size, Color squadColor)
        {
            try
            {
                string path = $"{ConfigData.GetCachePath()}{type}_{Type}_{ColorUtility.ToHtmlStringRGB(squadColor)}_{index}.png";
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(size.x, size.y);
                texture.filterMode = FilterMode.Point;
                texture.LoadImage(bytes);
                //Debug.Log($"Loaded cached sprites from {path} for Fleetship {Name}");
                return Sprite.Create(texture, new Rect(0, 0, size.x, size.y), ConfigData.HalfSize, ConfigData.PixelsPerUnit);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error while trying to load cached sprites: {e}");
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
                export.filterMode = FilterMode.Point;
                export.SetPixels(pixels);
                export.Apply();
                File.WriteAllBytesAsync(path, export.EncodeToPNG());
            }
            HasCachedSprite = true;

        }
        private void GetStats()
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
        public bool IsShipAlive()
        {
            return !IsDead;
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

        /// <summary>
        /// Returns the max Tsv of this ship tpye
        /// </summary>
        /// <returns></returns>
        public int GetTsv() 
        {
            return Utilities.GetMaxTsv(this.Type);
        }
        public int GetCapacity()
        {
            //Debug.Log($"Calculating TSV for {Type}. Firepower: {Firepower}");
            return GetTsv();
        }
        private FleetShip _fleetShip;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _fleetShip = obj as FleetShip;
            if (_fleetShip == null)
            {
                return false;
            }

            return Id == _fleetShip.Id;
        }

        public bool Equals(FleetShip other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(FleetShip a, FleetShip b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(FleetShip a, FleetShip b)
        {
            return !(a == b);
        }
        public string ToJson()
        {
            return $"{{\"i\": {Id}, \"n\": \"{Name}\", \"t\": {(int) Type}, \"d\": {(IsDead ? 1 : 0)}," +
                $"\"s\": {(HasCachedSprite ? 1 : 0)}, \"f\": {ShotsFired}, \"dd\": {DamageDone}, \"r\": {DamageReceived}, \"k\": {Kills}, \"b\": {BattlesFought}, \"w\": {BattlesWon}, " +
                $"\"m\": {MineralsMined}}}";
        }
        public override string ToString()
        {
            return $"#{Id} ({Type}) - {Name} ({(IsDead ? "D" : "A")}) ({(DoesBelongToSavedSquad ? "Y" : "N")}) ({(HasCachedSprite ? "Y" : "N")})";
        }

    }
}