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
        public readonly DateTime StartTime = DateTime.Now;
        public Level Level;
        public Stage Stage;
        public int Side;

        public Collider2D Collider;
        public Rigidbody2D Body;
        //protected virtual void Update() // [alert] [training] Should probably just remove this during training
        //{
        //    if (!Level.IsTraining && !Level.IsPaused)
        //    {
        //        Age++;
        //    }
        //}        
        public int GetLifeTime() {
            return DateTime.Now.Subtract(StartTime).Seconds;
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
        public void SetAngleTowardsPoint(Vector2 point)
        {
            transform.eulerAngles = new Vector3(0, 0, GetDegreesTowardsPoint(point));
        }
        /// <summary>
        /// Gets the angle (0-360) in degrees
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            float radians = AngleToPoint(point);
            float degrees = radians * Mathf.Rad2Deg;
            //Debug.Log($"Angle towards movement point before adjustment {degrees}");
            if (degrees > 0) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees - 180);

            }
            if (degrees < 0) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees) + 180;
            }
            //Debug.Log($"Angle towards movement point after adjustment {degrees}");
            return degrees;
        }
        public float GetRotatedAngleToPoint(Vector2 point)
        {
            float result = GetDegreesTowardsPoint(point) - GetRotation();
            if (result < 0)
            {
                result += 360;
            }
            return result;
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
            return transform.localPosition;
        }
        public float GetRotation()
        {
            return transform.eulerAngles.z;
        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Entity x = obj as Entity;
            if (x == null)
            {
                return false;
            }

            return Id == x.Id;
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
    }

}

