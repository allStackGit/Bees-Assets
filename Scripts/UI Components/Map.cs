using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    public class Map : MonoBehaviour
    {
        public GameObject FogOfWar;
        public SpriteRenderer SpriteRenderer;
        public int MaxZoom, MinZoom, MiniMapCameraSize;
        public Vector2 UserStartingPosition, AIStartingPosition;
        public string Name;
        public int Index;

        public void Setup(int index, string name, Vector2 userStartingPosition, Vector2 aiStartingPosition)
        {
            Index = index;
            Name = name;
            UserStartingPosition = userStartingPosition;
            AIStartingPosition = aiStartingPosition;
        }
    }
}