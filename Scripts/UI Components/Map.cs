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
            if (stage.IsTraining)
            {
                Destroy(FogOfWar);
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