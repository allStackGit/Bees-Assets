using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class Map
    {
        public int Id;
        public Vector2 UserStartingPosition, AIStartingPosition;
        public string Name;

        public Map(int id, Vector2 userStartingPosition, Vector2 aiStartingPosition, string name)
        {
            Id = id;
            UserStartingPosition = userStartingPosition;
            AIStartingPosition = aiStartingPosition;
            Name = $"{name} - #{Id}";
        }

        public override string ToString()
        {
            return Name;
        }
    }
}