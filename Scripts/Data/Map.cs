using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class Map
    {
        public int Id;
        public Vector2 UserStartingPosition, AIStartingPosition;
        public ConfigData.Locations Location;
        public string Name;

        public Map(int id, Vector2 userStartingPosition, Vector2 aiStartingPosition, ConfigData.Locations location)
        {
            Id = id;
            UserStartingPosition = userStartingPosition;
            AIStartingPosition = aiStartingPosition;
            Location = location;
            Name = $"{location} - #{Id}";
        }

        public override string ToString()
        {
            return Name;
        }
    }
}