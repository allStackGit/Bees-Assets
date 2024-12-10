using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class ObstacleMap 
    {
        public int Id;
        public string Name;

        public ObstacleMap(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }
}