using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = System.Random;

namespace Assets.Scripts
{
    
    public static class Utilities
    {
        public static readonly Dictionary<string, string> ShipNamesAndTypes = new Dictionary<string, string>()
        {
            {"A", "Queen" },
            {"B", "Hornet" },
            {"C", "Dreadnought" },
            {"D", "Gunship" },
            {"E", "Scout" },
            {"F", "Wasp" },
            {"G", "Bumblebee" },
            {"H", "Flagship" },
            {"I", "Honeybee" },
            {"J", "Carpenter Bee" },
            {"K", "Leafcutter" },
            {"L", "Yellow Jacket" },
            {"M", "Beehive" },
            {"N", "Frigate" },
            {"O", "Carrier" },
            {"P", "Drone" },
            {"Q", "Striker" },
            {"R", "Factory" },
            {"S", "Cruiser" },
            {"T", "Barge" },
            {"U", "Fire Ship" },
            {"V", "Warp Gate" },
        };
        public static readonly Dictionary<string, int> ShipTypeToInt = new Dictionary<string, int>()
        {
            {"A", 1 },
            {"B", 2 },
            {"C", 3 },
            {"D", 4 },
            {"E", 5 },
            {"F", 6 },
            {"G", 7 },
            {"H", 8 },
            {"I", 9 },
            {"J", 10 },
            {"K", 11 },
            {"L", 12 },
            {"M", 13 },
            {"N", 14 },
            {"O", 15 },
            {"P", 16 },
            {"Q", 17 },
            {"R", 18 },
            {"S", 19 },
            {"T", 20 },
            {"U", 21 },
            {"V", 22 },
        };

        private static readonly Random _rnd = new Random();
        public static int Hash()
        {
            return RandomInt(1000000000); // 1bil
        }
        public static long UniqueHash()
        {
            long hash = RandomLong(1000000000) * RandomLong(1000); // 1tril
            while (ConfigData.UsedHashes.Contains(hash))
            {
                Debug.Log($"A duplicate hash was found! {hash}");
                hash = RandomLong(1000000000) * RandomLong(1000);
            }
            ConfigData.UsedHashes.Add(hash);
            return hash;
        }

        public static bool AreVectorsEqual(Vector2 a, Vector2 b)
        {
            return Math.Floor(a.x) == Math.Floor(b.x) && Math.Floor(a.y) == Math.Floor(b.y);
        }
        public static Random GetRandom()
        {
            return _rnd;
        }
        /// <summary>
        /// Returns an integer between 0 (inclusive) and max (exclusive)
        /// </summary>
        public static int RandomInt(int max) 
        {
            return _rnd.Next(max);
        }
        public static long RandomLong(int max)
        {
            return (long) _rnd.Next(max);
        }
        /// <summary>
        /// Returns a float between 0 (inclusive) and max (exclusive)
        /// </summary>
        public static float RandomFloat(float max)
        {
            return (float) _rnd.NextDouble() * max;
        }
        public static int RandomSign()
        {
            //Debug.Log($"Random sign: {(r > 0 ? 1 : -1)}");
            return RandomInt(2) > 0 ? 1 : -1;
        }
        public static bool CoinToss()
        {
            return RandomInt(2) == 0;
        }
        // not strictly speaking the maximum and minimum distance, but the max change in x or y
        public static Vector2 RandomCoordinate(LevelStage level, Vector2 position, Vector2 maxDistance, Vector2 minDistance)
        {
            //Debug.Log($"maxDistance: {maxDistance}, minDistance: {minDistance}");
            Vector2 newLocation = Vector2.zero;
            int loops = 0;
            while ((newLocation == Vector2.zero || !VectorInBounds(level, newLocation)) && loops < 35){
                newLocation = new Vector2(position.x + (RandomFloat(maxDistance.x) + minDistance.x) * RandomSign(), position.y + (RandomFloat(maxDistance.y) + minDistance.y) * RandomSign());
                loops++;
            }
            if (loops == 100)
            {
                Debug.Log($"Couldn't find a random coordinate that was in bounds: {newLocation}, {minDistance}, {maxDistance}");
            }
            return newLocation;

        }
        public static bool VectorInBounds(LevelStage level, Vector2 vector)
        {
            return (vector.x > level.MinX && vector.x < level.MaxX && vector.y > level.MinY && vector.y < level.MaxY);
        }
        public static float Random(float max)
        {
            return (float) _rnd.NextDouble() * max;
        }

        public static float AngleBetweenPoints(Vector2 a, Vector2 b)
        {
            return (Mathf.Atan2(a.x - b.x, a.y - b.y));
        }

        public static Vector2 DirectionBetweenPoints(Vector2 a, Vector2 b)
        {
            return (a - b).normalized;
        }

        public static float AngleBetweenThreePoints(Vector2 a, Vector2 b, Vector2 c)
        {
            return AngleBetweenPoints(c, a) - AngleBetweenPoints(b, a);
        }

        public static string ConvertShipTypeToName(string shipType)
        {
            
            if (shipType.StartsWith("Type "))
            {
                shipType = shipType.Substring(5);
            }
            return ShipNamesAndTypes.GetValueOrDefault(shipType);
            
        }
        public static string ConvertShipTypeToPluralName(string shipType)
        {
            string name = ConvertShipTypeToName(shipType);
            if (name == "Queen")
            {
                return name;
            }
            else
            {
                return $"{name}s";
            }
        }
        public static string ConvertShipNameToType(string shipName) // [alert] switch to alphabetical order for ship type codes or maybe even drop them entirely
        {
            return ShipNamesAndTypes.FirstOrDefault((v) => v.Value == shipName).Key;
        }
        
        public static string GenerateCommanderName()
        {
            return $"{GenerateName()} {GenerateName()}";
        }
        public static string GenerateName(int length = 0)
        {
            if (length == 0)
            {
                length = RandomInt(5)+1;
            }
            string[] consonants = { "b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "l", "n", "p", "q", "r", "s", "sh", "zh", "t", "v", "w", "x" };
            string[] vowels = { "a", "e", "i", "o", "u", "ae", "y" };
            string name = "";
            name += consonants[_rnd.Next(consonants.Length)].FirstCharacterToUpper();
            name += vowels[_rnd.Next(vowels.Length)];
            int lettersAdded = 2;
            while (lettersAdded < length)
            {
                name += consonants[_rnd.Next(consonants.Length)];
                lettersAdded++;
                name += vowels[_rnd.Next(vowels.Length)];
                lettersAdded++;
                
            }
            foreach (string word in ConfigData.Configuration.CensoredWords)
            {
                if (name.ContainsInsensitive(word))
                {
                    return GenerateName(length);
                }
            }
            
            return name;
        }
        // These are different from the camera methods because they convert the values rather than the coordinates. The camera will tell you where a unit is on the world or
        // screen and this will tell you the value of the unit
        public static Vector2 WorldUnitsToScreenPixels(Vector2 vector, Camera camera)
        {
            Vector2 baseWorldPoint = camera.WorldToScreenPoint(new Vector2(0, 0));
            Vector2 screenPoint = camera.WorldToScreenPoint(vector);
            return new Vector2(Mathf.Abs(baseWorldPoint.x - screenPoint.x), Mathf.Abs(baseWorldPoint.y - screenPoint.y));
        }
        public static Vector2 ScreenPixelsToWorldUnits(Vector2 vector, Camera camera)
        {
            Vector2 baseWorldPoint = camera.ScreenToWorldPoint(new Vector2(0, 0));
            Vector2 worldPoint = camera.ScreenToWorldPoint(vector);
            return new Vector2(Mathf.Abs(baseWorldPoint.x - worldPoint.x), Mathf.Abs(baseWorldPoint.y - worldPoint.y));
        }
        public static void WriteJsonFile(string contents)
        {
            string path = $"{ConfigData.GetBasePath()}/{Hash()}.json";
            File.WriteAllText(path, contents);
        }
        public static void WriteTextFile(string contents)
        {
            string path = $"{ConfigData.GetBasePath()}/{Hash()}.txt";
            File.WriteAllText(path, contents);
        }
        public static int[] SetChangablePixelsForImage(Color[] colors, Sprite sprite)
        {
            Texture2D sourceTexture = sprite.texture;
            Color[] pixels = sourceTexture.GetPixels();
            List<int> indexes = new List<int>();

            //Debug.Log($"Pixels: {pixels}, {pixels.Length}, {pixels[0]}, color: {colors.Length}, {colors[0]}");

            for (int c = 0; c < colors.Length; c++)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (Vector4.Distance(pixels[i], colors[c]) < .001f)
                    {
                        //Debug.Log($"Found matching color {colors[c]} at {i}");
                        indexes.Add(i);
                    }
                    else
                    {
                        //if (pixels[i].a > .99 && pixels[i].g > .01 && i % 10000 == 0)
                        //{
                        //    Debug.Log($"Color is too far apart: {pixels[i]} != {colors[c]} at {i}");
                        //}
                    }
                }
            }


            return indexes.ToArray();
        }

        public static Sprite SetImageColor(Color color, Sprite sprite, int[] changablePixels)
        {

            Texture2D sourceTexture = sprite.texture;
            Vector2 dimensions = new Vector2(sourceTexture.width, sourceTexture.height);
            Color[] pixels = sourceTexture.GetPixels();


            for (int i = 0; i < changablePixels.Length; i++)
            {
                pixels[changablePixels[i]] = color;
            }
            Texture2D changedTexture = new Texture2D(sourceTexture.width, sourceTexture.height);

            changedTexture.SetPixels(pixels);
            changedTexture.Apply(true);
            //Debug.Log($"width: {dimensions.x}, height: {dimensions.y}");
            return Sprite.Create(changedTexture, new Rect(0, 0, sourceTexture.width, sourceTexture.height), (dimensions / dimensions) / 2, ConfigData.PixelsPerUnit);
        }

        public static Vector2 ForceBounds(float x, float y, float MaxX, float MaxY, float MinX, float MinY)
        {
            return new Vector2(Mathf.Clamp(x, MinX, MaxX), Mathf.Clamp(y, MinY, MaxY));
        }

        public static Vector2 RotatePointAroundPoint(Vector2 pivot, Vector2 rotatedPoint, float radians)
        {

            float cosAngle = Mathf.Cos(radians);
            float sinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            Vector2 translatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            float rotatedX = translatedVector.x * cosAngle - translatedVector.y * sinAngle;
            float rotatedY = translatedVector.x * sinAngle + translatedVector.y * cosAngle;

            // Translate the rotated vector back to the original position
            Vector2 rotatedVector = new Vector2(rotatedX, rotatedY) + pivot;

            return rotatedVector;
        }

        public static Vector2Int RotateIntPointAroundPoint(Vector2Int pivot, Vector2Int rotatedPoint, float radians)
        {

            double cosAngle = Mathf.Cos(radians);
            double sinAngle = Mathf.Sin(radians);

            // Translate the original vector to be relative to the pivot
            Vector2Int translatedVector = rotatedPoint - pivot;

            // Rotate the translated vector
            double rotatedX = translatedVector.x * cosAngle - translatedVector.y * sinAngle;
            double rotatedY = translatedVector.x * sinAngle + translatedVector.y * cosAngle;

            // Translate the rotated vector back to the original position
            Vector2Int rotatedVector = new Vector2Int(Convert.ToInt32(rotatedX), Convert.ToInt32(rotatedY)) + pivot;

            return rotatedVector;
        }

        /// <summary>
        /// Rotates the game object on this ship the quickest way towards a rotation and returns true once it reaches that rotation. Returns false once it is done rotating
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <param name="rotationSpeed"></param>
        /// <returns></returns>
        public static bool TimedRotation(GameObject entity, float rotation, float rotationSpeed)
        {
            float difference = Mathf.DeltaAngle(entity.transform.eulerAngles.z, rotation);
            float closeEnough = 3;
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (difference > closeEnough)
            {
                entity.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed * 1));
                return false;
            }
            else if (difference < (0 - closeEnough))
            {
                entity.transform.Rotate(new Vector3(0, 0, 1 * Time.fixedDeltaTime * rotationSpeed * -1));
                return false;
            }
            else
            {
                entity.transform.eulerAngles = new Vector3(0, 0, rotation);
                return true;
            }
        }
        /// <summary>
        /// Checks to see if a game object is rotated towards a rotation or not
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        public static bool IsAimedAt(GameObject entity, float rotation)
        {
            float difference = Mathf.DeltaAngle(entity.transform.eulerAngles.z, rotation);
            float closeEnough = 3;
            //Debug.Log($"Difference in angles {difference}, {(difference > closeEnough ? "counter-clockwise" : "clockwise")}");
            if (difference > closeEnough)
            {
                return false;
            }
            else if (difference < (0 - closeEnough))
            {
                return false;
            }
            else
            {
                return true;
            }
        }
        public static string ListToString<T>(List<T> list)
        {
            string str = "";
            list.ForEach(r => str += $"{r}, ");
            if (str.Length > 2)
            {
                str = str.Remove(str.Length - 2);
            }
            return str;
        }
        public static void SetUIColor(GameObject gameObject, Color color)
        {
            Image image = gameObject.GetComponent<Image>();
            if (image != null)
            {
                image.color = color;
            }
            else
            {
                SpriteRenderer sprite = gameObject.GetComponent<SpriteRenderer>();
                if (sprite != null)
                {
                    sprite.color = color;
                }
                else
                {
                    Debugger.Exception($"Tried to set the color of {gameObject.name} which doesn't have a UI image.");
                }
            }
            
        }
        public static void SetBadColor(GameObject gameObject)
        {
            SetUIColor(gameObject, ConfigData.GetUIColor("bad"));
        }
        public static void SetGoodColor(GameObject gameObject)
        {
            SetUIColor(gameObject, ConfigData.GetUIColor("good"));
        }

        public static List<dynamic> JArrayToDynamicList(dynamic jArray)
        {
            return ((JArray)jArray).ToList<dynamic>();
        }
        public static List<T> JArrayToList<T>(dynamic jArray)
        {
           return ((JArray)jArray).ToList<dynamic>().ConvertAll((item) => (T)item);
        }
        public static T[] JArrayToArray<T>(dynamic jArray)
        {
            return Array.ConvertAll(((JArray)jArray).ToArray<dynamic>(), (item) => (T)item);
        }
        public static Dictionary<K, V> JArrayToDictionary<K, V>(dynamic jArray)
        {
            Dictionary<K, V> dictionary = new Dictionary<K, V>();
            List<dynamic> list = JArrayToList<dynamic>(jArray);
            list.ForEach((item) =>
            {
                Dictionary<K, V> d = ((JObject)item).ToObject<Dictionary<K, V>>();
                dictionary.Add(d.Keys.First(), d.Values.First());
            });
            return dictionary;
        }
        public static int CalculateCarrierAdditionalTsv()
        {
            FleetShip striker = new FleetShip(-1, ConfigData.Configuration.HumanSide, "", "Striker", false, false, 0, 0, 0, 0, 0, 0);
            FleetShip drone = new FleetShip(-1, ConfigData.Configuration.HumanSide, "", "Drone", false, false, 0, 0, 0, 0, 0, 0);

            return (striker.GetTsv() * ConfigData.Configuration.CarrierCarryStrikerMax) + (drone.GetTsv() * ConfigData.Configuration.CarrierCarryDroneMax);
        }
        public static int CalculateTsv(Ship ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateMaxTsv(Ship ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.Health, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateMaxTsv(FleetShip ship)
        {
            return CalculateTsv(ship.Speed, ship.Firepower, ship.MaxHealth, ship.Sight, ship.AdditionalTsv);
        }
        public static int CalculateTsv(float speed, float firepower, int health, int sight, int additionalTsv)
        {
            double speedValue = speed / 3;
            int fullHealthTsv = (int)Math.Round((firepower > 0 ? firepower : 1) * (speedValue > 1 ? speedValue : 1) * (health / 200), 0) + sight;
            return ((health > 0 ? 1 : 0) * fullHealthTsv) + ((health > 0 ? 1 : 0) * (health + additionalTsv));
        }
        public static float CalculateFirepower(int power, int range, float rateOfFire, float rotationRate, float ProjectileValue, float specialFirepower)
        {
            //Debug.Log($"Power: {(power * ProjectileValue)}, DPS: {((power * ProjectileValue) / rateOfFire)}, Range: {Mathf.Pow((range / 20), 2)}");
            return rateOfFire > 0 ? ((((power*ProjectileValue) / rateOfFire) * Mathf.Clamp(rotationRate/128, .5f, 1.25f)) * Mathf.Pow((range / 20), 2)) : specialFirepower;
        }
        /// <summary>
        /// Linecasts from start to end to check for any obstacles in the path. Returns true if there are obstacles in the way
        /// </summary>
        /// <param name="destination"></param>
        /// <returns></returns>
        public static bool HasObstaclesInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstaclesLayer).collider != null;
        }

        /// <summary>
        /// Linecasts from start to end to check for any obstacles in the path. Returns true if there are obstacles or obstacle proximity ranges in the way
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public static bool HasObstaclesCloseToInTheWay(Vector2 start, Vector2 end)
        {
            return Physics2D.Linecast(start, end, ConfigData.ObstacleProximityRangesLayer).collider != null;
        }
        public static void Print2DArray(bool[][] array)
        {
            string line = "";
            for (int x = 0; x < array.Length; x++)
            {
                for (int y = 0; y < array[x].Length; y++)
                {
                    line += $"{(array[y][x] ? "▢" : "■")}";
                }
                line += "\n";
            }
            WriteTextFile(line);
        }




    }
}