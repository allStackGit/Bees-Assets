using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class AsteroidPiece : Obstacle
    {
        public Rigidbody2D Body;
        public int Speed;
        public int HalfSeconds;
        private ScaledTimer _deathTimer = new ScaledTimer();
        public override void Create(Stage stage)
        {
            base.Create(stage);
            Speed = (Utilities.RandomInt(Level.Stage.AsteroidMaxSpeed) + ConfigData.MinimumAsteroidSpeed) + 5;

        }
        public override void ClearData()
        {
            base.ClearData();
            HalfSeconds = 0;
        }
        public void Setup(Level level, CollisionAsteroid parent)
        {
            base.Setup(level);


            transform.localPosition = parent.GetPosition();
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            Vector2 randomPoint = Utilities.RandomCoordinate(Level, Level.GetPosition(), new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.linearVelocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), randomPoint);
            Body.angularVelocity = parent.Body.angularVelocity;

            _deathTimer.Reuse(.5f, DeathTimer, true);
            Level.AddTimer(_deathTimer);
            //InvokeRepeating(nameof(DeathTimer), 1f, .5f);
        }
        public void DeathTimer()
        {
            if (HalfSeconds == 10)
            {
                Kill();
            }
            else
            {
                SpriteRenderer.color = new Color(SpriteRenderer.color.r, SpriteRenderer.color.g, SpriteRenderer.color.b, SpriteRenderer.color.a - .1f);
                HalfSeconds++;
            }
        }

        public void Kill()
        {
            if (!IsDead)
            {
                IsDead = true;
                Level.State.RemoveObstacle(this);
                Level.CancelTimer(_deathTimer);
                Debug.LogError($"No pool for asteroid pieces");
            }
        }
    }
}