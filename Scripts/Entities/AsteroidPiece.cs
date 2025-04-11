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
            Speed = (Utilities.RandomInt(Stage.AsteroidMaxSpeed) + ConfigData.MinimumAsteroidSpeed) + 5;

        }
        public override void ClearData()
        {
            base.ClearData();
            HalfSeconds = 0;
        }
        Vector2 _randomPoint;
        public void Setup(Level level, CollisionAsteroid parent)
        {
            base.Setup(level);
            transform.parent = Level.Map.Transform;
            transform.localPosition = parent.GetPosition();
            transform.localEulerAngles = new Vector3(0, 0, Utilities.RandomInt(360));

            //Debug.Log($"Setup Asteroid {Name} with Speed: {Speed}, starting at {transform.localPosition}");
            _randomPoint = Utilities.RandomCoordinate(Level, Level.GetPosition(), new Vector2(Level.HalfMapWidth, Level.HalfMapHeight), Vector2.zero);
            Body.linearVelocity = Speed * -Utilities.DirectionBetweenPoints(GetPosition(), _randomPoint);
            Body.angularVelocity = parent.Body.angularVelocity;

            _deathTimer.Reuse(.5f, DeathTimer, true);
            Level.AddTimer(_deathTimer);
            Level.State.AddObstacle(this);
            //InvokeRepeating(nameof(DeathTimer), 1f, .5f);
        }
        public void DeathTimer()
        {
            if (HalfSeconds == 8)
            {
                Kill();
            }
            else
            {
                SpriteRenderer.color = ConfigData.FadingAsteroidPiecesColors[HalfSeconds];
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
                Level.State.AsteroidPiecesToRelease.Add(this);
                gameObject.SetActive(false);
            }
        }
    }
}