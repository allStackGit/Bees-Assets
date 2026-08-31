using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    public class Map : MonoBehaviour
    {
        public GameObject FogOfWar;
        public List<GameObject> Decorations;
        public SpriteRenderer SpriteRenderer;
        public int MaxZoom, MinZoom, MiniMapCameraSize;
        public Vector2 UserStartingPosition, AIStartingPosition;
        public string Name;
        public int Index;
        public ImageSpinner RingSparkle;
        public Transform Transform;
        public Vector2 SizeMultiplier = Vector2.zero;
        /// <summary>
        /// The Id of this map relative to the stage. Guarenteed unique for this stage.
        /// </summary>
        public int ItemId;

        public void Create(Stage stage, int index, int itemId, string name, Vector2 userStartingPosition, Vector2 aiStartingPosition)
        {
            Index = index;
            ItemId = itemId;
            Name = name;
            UserStartingPosition = userStartingPosition;
            AIStartingPosition = aiStartingPosition;

            SizeMultiplier = new Vector2(SpriteRenderer.size.x / 512, SpriteRenderer.size.y / 512);
            //Debug.Log($"Size multiplier for {Name} is {SizeMultiplier}");

            if (stage.IsTraining)
            {
                // Maps are pooled and SetupLevel reuses the FogOfWar reference every episode.
                // Keep the object inactive instead of destroying the serialized reference; otherwise
                // the first reset touches a destroyed GameObject and aborts ML-Agents stepping.
                if (FogOfWar != null)
                {
                    FogOfWar.SetActive(false);
                }
                Decorations.ForEach(d => Destroy(d));
                if (!stage.IsRendering)
                {
                    SpriteRenderer.enabled = false;
                }
            }
            Transform = transform;
        }

        public void Setup(Level level)
        {
            transform.parent = level.transform;
            transform.localPosition = Vector2.zero;

            // Map-border and other map-authored Obstacle components are part of the map prefab,
            // rather than objects obtained through Pool.Create. Give them their runtime ownership
            // before activating the map. Without this, MapBorder callbacks dereference a null Stage
            // on levels without pathfinding, while Pathfinder.Obstacle.Setup fails on the same
            // borders when a level (such as Pluto III) does initialize an obstacle grid.
            Obstacle[] mapObstacles = GetComponentsInChildren<Obstacle>(true);
            for (int i = 0; i < mapObstacles.Length; i++)
            {
                Obstacle obstacle = mapObstacles[i];
                if (obstacle == null)
                {
                    continue;
                }
                obstacle.Level = level;
                obstacle.Stage = level.Stage;
            }

            if (!level.Stage.IsTraining && RingSparkle != null)
            {
                RingSparkle.Setup(level);
            }
            gameObject.SetActive(true);
        }
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Map x = obj as Map;
            if (x == null)
            {
                return false;
            }

            return ItemId == x.ItemId;
        }
        public bool Equals(Map other)
        {
            return ItemId == other.ItemId;
        }

        public override int GetHashCode()
        {
            return ItemId.GetHashCode();
        }

        public static bool operator ==(Map a, Map b)
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

            return a.ItemId == b.ItemId;
        }

        public static bool operator !=(Map a, Map b)
        {
            return !(a == b);
        }
    }
}
