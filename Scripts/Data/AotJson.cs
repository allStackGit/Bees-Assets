using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Data
{
    /// <summary>
    /// Explicit Newtonsoft token parsing for data loaded during IL2CPP/WebGL bootstrap.
    /// Runtime-bound member dispatch requires code generation that is unavailable under IL2CPP.
    /// </summary>
    internal static class AotJson
    {
        public static JObject RequireObject(object value, string context)
        {
            if (value is JObject jsonObject)
            {
                return jsonObject;
            }

            throw new InvalidOperationException($"Expected JSON object while loading {context}.");
        }

        public static JArray RequireArray(object value, string context)
        {
            if (value is JArray jsonArray)
            {
                return jsonArray;
            }

            throw new InvalidOperationException($"Expected JSON array while loading {context}.");
        }

        public static List<ConfigData.ShipTypes> ParseShipTypes(JToken token)
        {
            List<ConfigData.ShipTypes> result = new List<ConfigData.ShipTypes>();
            if (!(token is JArray entries))
            {
                return result;
            }

            foreach (JToken entry in entries)
            {
                result.Add(Utilities.ConvertShipNameToShipType[entry.Value<string>()]);
            }
            return result;
        }

        public static Dictionary<string, int[]> ParseStringIntArrayDictionary(JToken token)
        {
            Dictionary<string, int[]> result = new Dictionary<string, int[]>();
            if (!(token is JArray entries))
            {
                return result;
            }

            foreach (JObject entry in entries.Children<JObject>())
            {
                foreach (JProperty property in entry.Properties())
                {
                    result.Add(property.Name, property.Value.ToObject<int[]>());
                }
            }
            return result;
        }

        public static List<FleetShip> ParseFleetShips(JArray entries)
        {
            List<FleetShip> result = new List<FleetShip>();
            foreach (JObject ship in entries.Children<JObject>())
            {
                result.Add(new FleetShip(
                    ship.Value<int>("i"),
                    (ConfigData.ShipTypes)ship.Value<int>("t"),
                    ship.Value<int>("s") == 1,
                    ship.Value<int>("d") == 1,
                    ship.Value<int>("f"),
                    ship.Value<int>("dd"),
                    ship.Value<int>("r"),
                    ship.Value<int>("k"),
                    ship.Value<int>("b"),
                    ship.Value<int>("w"),
                    ship["m"]?.Value<int>() ?? 0,
                    ship["n"]?.Value<string>() ?? string.Empty));
            }
            return result;
        }

        public static List<SavedSquad> ParseSavedSquads(JToken token)
        {
            List<SavedSquad> result = new List<SavedSquad>();
            if (!(token is JArray entries))
            {
                return result;
            }

            foreach (JObject squad in entries.Children<JObject>())
            {
                JObject colorJson = (JObject)squad["Color"];
                JObject statsJson = (JObject)squad["Stats"];
                JObject startingPositionJson = (JObject)squad["StartingPosition"];

                Color color = new Color(
                    colorJson.Value<float>("r"),
                    colorJson.Value<float>("g"),
                    colorJson.Value<float>("b"),
                    colorJson.Value<float>("a"));
                SquadStatBlock stats = new SquadStatBlock(
                    statsJson.Value<string>("Commander"),
                    statsJson.Value<int>("BattlesFought"),
                    statsJson.Value<int>("BattlesWon"),
                    statsJson.Value<int>("ShipsLost"),
                    statsJson.Value<int>("DamageDone"),
                    statsJson.Value<int>("DamageReceived"),
                    statsJson.Value<int>("Kills"));
                SavedSquad savedSquad = new SavedSquad(
                    squad.Value<long>("Id"),
                    squad.Value<int>("Side"),
                    squad.Value<string>("Name"),
                    new Vector2(
                        startingPositionJson.Value<float>("x"),
                        startingPositionJson.Value<float>("y")),
                    squad.Value<bool>("CeaseFire"),
                    squad.Value<bool>("IsMatchingSpeed"),
                    Utilities.ConvertShootingStrategyNameToType[squad.Value<string>("ChosenShootingStrategy")],
                    color,
                    stats);

                if (squad["Ships"] is JArray ships)
                {
                    foreach (JObject ship in ships.Children<JObject>())
                    {
                        JObject offsetJson = (JObject)ship["Offset"];
                        savedSquad.AddShipToSquad(new SquadShip(
                            ship.Value<long>("FleetId"),
                            Utilities.ConvertShipNameToShipType[ship.Value<string>("ShipType")],
                            new Vector2(
                                offsetJson.Value<float>("x"),
                                offsetJson.Value<float>("y"))));
                    }
                }

                result.Add(savedSquad);
            }
            return result;
        }

        public static List<(Vector2, Vector2)> ParseObstacles(JToken token)
        {
            List<(Vector2, Vector2)> result = new List<(Vector2, Vector2)>();
            if (!(token is JArray entries))
            {
                return result;
            }

            foreach (JObject obstacle in entries.Children<JObject>())
            {
                JObject position = (JObject)obstacle["Position"];
                JObject scale = (JObject)obstacle["Scale"];
                result.Add((
                    new Vector2(position.Value<float>("x"), position.Value<float>("y")),
                    new Vector2(scale.Value<float>("x"), scale.Value<float>("y"))));
            }
            return result;
        }
    }
}
