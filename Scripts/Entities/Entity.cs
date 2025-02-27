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
        //public readonly DateTime StartTime = DateTime.Now;
        public Level Level;
        public Stage Stage;
        public int Side;

        public Collider2D Collider;
        public Rigidbody2D Body;
        public SpriteRenderer SpriteRenderer;
        public Transform Transform;
        /// <summary>
        /// The rotation of the entity, in degrees
        /// </summary>
        public float Rotation;
        //protected virtual void Update() // [alert] [training] Should probably just remove this during training
        //{
        //    if (!Level.IsTraining && !Level.IsPaused)
        //    {
        //        Age++;
        //    }
        //}        
        //public int GetLifeTime() {
        //    return DateTime.Now.Subtract(StartTime).Seconds;
        //}
        public virtual void Create(Stage stage)
        {
            Stage = stage;
            if (!Stage.IsRendering)
            {
                Destroy(SpriteRenderer);
            }
            Rotation = Transform.eulerAngles.z;
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
        /// <summary>
        /// Calculates the angle to a point in radians
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float AngleToPoint(Vector2 point) 
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        public Vector2 DirectionToPoint(Vector2 point)
        {
            return Utilities.DirectionBetweenPoints(GetPosition(), point);
        }
        //private float _degrees;
        //private Vector2 _direction;
        private Vector2 _position;
        /// <summary>
        /// Gets the angle (0-360) in degrees
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            //_degrees = AngleToPoint(point) * Mathf.Rad2Deg;
            ////Debug.Log($"Angle towards movement point before adjustment {degrees}");
            //if (_degrees > 0) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
            //{
            //    _degrees = Mathf.Abs(_degrees - 180);

            //}
            //else if (_degrees < 0) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
            //{
            //    _degrees = Mathf.Abs(_degrees) + 180;
            //}
            ////Debug.Log($"Angle towards movement point after adjustment {_degrees}");
            //return _degrees;

            _position = GetPosition();
            return Mathf.Repeat(-Mathf.Atan2(point.x - _position.x, point.y - _position.y) * Mathf.Rad2Deg, 360f);



        }
        //private float _result;
        //public float GetRotatedAngleToPoint(Vector2 point)
        //{
        //    _result = GetDegreesTowardsPoint(point) - GetRotation();
        //    if (_result < 0)
        //    {
        //        _result += 360;
        //    }
        //    return _result;
        //}
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
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _entity = obj as Entity;
            if (_entity == null)
            {
                return false;
            }

            return Id == _entity.Id;
        }

        public bool Equals(Entity other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(Entity a, Entity b)
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

        public static bool operator !=(Entity a, Entity b)
        {
            return !(a == b);
        }
        public virtual void Activate()
        {
            //gameObject.SetActive(true);
            //Collider.enabled = true;
            Body.simulated = true;
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = true;
            }
            enabled = true;
        }
        public virtual void Deactivate()
        {
            //gameObject.SetActive(false);
            //Collider.enabled = false;
            Body.simulated = false;
            if (Stage.IsRendering)
            {
                SpriteRenderer.enabled = false;
            }
            enabled = false;
        }
    }

}

