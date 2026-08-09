using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;

using UnityEngine;
using UnityEngine.UIElements;

namespace Assets.Scripts.Entities
{
    public class Entity : MonoBehaviour
    {
        public long Id;
        public long Age;
        public Level Level;
        public Stage Stage;
        public int Side;

        public Collider2D Collider;
        public Rigidbody2D Body;
        public SpriteRenderer SpriteRenderer;
        public Transform Transform;
        public float OriginalRotation, Rotation;

        public virtual void Create(Stage stage)
        {
            Stage = stage;
            if (!Stage.IsRendering)
            {
                Destroy(SpriteRenderer);
            }
            OriginalRotation = Transform.eulerAngles.z;
            Rotation = OriginalRotation;
        }
        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }
        public float DistanceTo(Entity entity)
        {
            return DistanceToPoint(entity.GetPosition());
        }
        public float AngleTo(Entity entity)
        {
            return AngleToPoint(entity.GetPosition());
        }
        public float AngleToPoint(Vector2 point) 
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        public Vector2 DirectionToPoint(Vector2 point)
        {
            return Utilities.DirectionBetweenPoints(GetPosition(), point);
        }
        private Vector2 _position;
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            _position = GetPosition();
            return Mathf.Repeat(-Mathf.Atan2(point.x - _position.x, point.y - _position.y) * Mathf.Rad2Deg, 360f);
        }
        public bool IsFriendly(Entity entity)
        {
            return Side == entity.Side;
        }
        public float GetX()
        {
            return GetPosition().x;
        }
        public float GetY()
        {
            return GetPosition().y;
        }
        public Vector2 GetPosition()
        {
            return Transform.localPosition;
        }

        private Entity _entity;
        public override bool Equals(System.Object obj)
        {
            if (System.Object.ReferenceEquals(this, obj))
            {
                return true;
            }
            if ((UnityEngine.Object)this == null || obj == null)
            {
                return false;
            }

            _entity = obj as Entity;
            if ((UnityEngine.Object)_entity == null)
            {
                return false;
            }

            return Id == _entity.Id;
        }

        public bool Equals(Entity other)
        {
            if (System.Object.ReferenceEquals(this, other))
            {
                return true;
            }
            if ((UnityEngine.Object)this == null || (UnityEngine.Object)other == null)
            {
                return false;
            }
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(Entity a, Entity b)
        {
            bool aIsUnityNull = System.Object.ReferenceEquals(a, null) || (UnityEngine.Object)a == null;
            bool bIsUnityNull = System.Object.ReferenceEquals(b, null) || (UnityEngine.Object)b == null;

            if (aIsUnityNull || bIsUnityNull)
            {
                return aIsUnityNull && bIsUnityNull;
            }

            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(Entity a, Entity b)
        {
            return !(a == b);
        }
        public virtual void Activate()
        {
            Body.simulated = true;
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = true;
            }
            enabled = true;
        }
        public virtual void Deactivate()
        {
            Body.simulated = false;
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = false;
            }
            enabled = false;
        }
    }
}
