using Assets.Scripts.Scenes;
using System;

using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Entity : MonoBehaviour
    {
        public long Id;
        public long Age;
        public readonly DateTime StartTime = DateTime.Now;
        public LevelStage Level;
        public int Side;

        public Rigidbody2D Body;
        protected virtual void Update()
        {
            if (!Level.IsPaused)
            {
                Age++;
            }
        }        
        public bool Equals(Entity entity)
        {
            return Id == entity.Id;
        }
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
        public Vector2 CirclePoint(float angle, float distance)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            Vector2 point = new Vector2((GetX() + (Mathf.Cos(angle) * distance)),
                (GetY() + (Mathf.Sin(angle) * distance)));
            return point;
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
        private void OnDestroy()
        {
            CancelInvoke();
        }
    }

}

