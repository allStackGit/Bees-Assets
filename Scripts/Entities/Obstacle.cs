using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class Obstacle : MonoBehaviour
    {
        public int Health, OriginalHealth, Id;
        /// <summary>
        /// The index of the obstacle in the list of obstacle points for the pathfinding map
        /// </summary>
        public int MapPointsIndex;
        public string Name;
        public bool IsDead;
        public Level Level;
        public Stage Stage;
        public ConfigData.ObstacleTypes ObstacleType;
        public Collider2D Collider, ProximityCollider, ClearanceMappingCollider;
        public SpriteRenderer SpriteRenderer;
        public SpriteMask SpriteMask;
        private bool _breakApartStarted;

        public virtual void Create(Stage stage)
        {
            Stage = stage;
            OriginalHealth = Health;
            if (!Stage.IsRendering)
            {
                Destroy(SpriteRenderer);
            }
        }
        public virtual void Setup(Level level)
        {
            if (level == null)
            {
                throw new System.ArgumentNullException(nameof(level));
            }

            // Setup already receives the owning Level, so Stage ownership must be derived from it
            // rather than relying on every spawn path to have called Create first. Authored obstacle
            // prefabs (for example Pluto III's Pushback layout) are instantiated directly beneath
            // the map and historically reached Pathfinder.Setup with a null serialized Stage.
            Level = level;
            Stage = level.Stage;
            if (Stage == null)
            {
                throw new System.InvalidOperationException(
                    $"Cannot set up obstacle '{gameObject.name}' because its owning level has no Stage.");
            }

            // Fresh authored StaticObstacle prefabs serialize Health but have OriginalHealth == 0
            // because they do not pass through the pooled Create lifecycle. Preserve their authored
            // health before the normal setup reset so they do not become zero-health obstacles.
            if (OriginalHealth <= 0 && Health > 0)
            {
                OriginalHealth = Health;
            }

            ClearData();
            Id = Level.State.GetId();
            if (!Stage.IsTraining)
            {
                Name = $"{ObstacleType} #{Id}";
                gameObject.name = Name;
            }
            Health = OriginalHealth;
            gameObject.SetActive(true);

        }
        public virtual void ClearData()
        {
            //Debug.Log($"Clearing data for {Name}");
            MapPointsIndex = 0;
            IsDead = false;
            _breakApartStarted = false;
        }

        /// <summary>
        /// Spawns cosmetic debris across this obstacle and then removes the obstacle normally.
        /// Debris uses isolated deterministic randomness so the visual effect does not advance
        /// Unity's gameplay random state.
        /// </summary>
        public virtual void BreakApart(Vector2 explosionPosition, Sprite[] debrisSprites,
            int debrisCount, float minSpeed, float maxSpeed, float maxSpin,
            float lifetime, float damping, float minScale, float maxScale)
        {
            if (_breakApartStarted || IsDead)
            {
                return;
            }

            _breakApartStarted = true;

            if (Stage != null && Stage.IsRendering && debrisSprites != null && debrisSprites.Length > 0 &&
                debrisCount > 0 && Level != null)
            {
                Bounds bounds;
                if (Collider != null)
                {
                    bounds = Collider.bounds;
                }
                else if (SpriteRenderer != null)
                {
                    bounds = SpriteRenderer.bounds;
                }
                else
                {
                    bounds = new Bounds(transform.position, Vector3.one);
                }

                int seed = unchecked((Id * 397) ^ explosionPosition.x.GetHashCode() ^ explosionPosition.y.GetHashCode());
                System.Random random = new System.Random(seed);
                ObstacleDebrisPool debrisPool = ObstacleDebrisPool.GetOrCreate(Stage);

                for (int i = 0; i < debrisCount; i++)
                {
                    Sprite sprite = debrisSprites[random.Next(debrisSprites.Length)];
                    if (sprite == null)
                    {
                        continue;
                    }

                    Vector2 spawnPosition = new Vector2(
                        Mathf.Lerp(bounds.min.x, bounds.max.x, NextFloat(random)),
                        Mathf.Lerp(bounds.min.y, bounds.max.y, NextFloat(random)));

                    Vector2 direction = spawnPosition - explosionPosition;
                    if (direction.sqrMagnitude < 0.01f)
                    {
                        float angle = NextFloat(random) * Mathf.PI * 2f;
                        direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    }
                    else
                    {
                        direction.Normalize();
                    }

                    Vector2 spread = new Vector2(NextSignedFloat(random), NextSignedFloat(random)) * 0.35f;
                    direction = (direction + spread).normalized;

                    float speed = Mathf.Lerp(minSpeed, maxSpeed, NextFloat(random));
                    float spin = Mathf.Lerp(-maxSpin, maxSpin, NextFloat(random));
                    float pieceLifetime = lifetime * Mathf.Lerp(0.8f, 1.2f, NextFloat(random));
                    float scale = Mathf.Lerp(minScale, maxScale, NextFloat(random));

                    ObstacleDebrisPiece debrisPiece = debrisPool.Get();
                    debrisPiece.transform.SetParent(Level.Map.Transform, true);
                    debrisPiece.transform.position = spawnPosition;
                    debrisPiece.transform.rotation = Quaternion.Euler(0f, 0f, NextFloat(random) * 360f);
                    debrisPiece.Setup(debrisPool, sprite, SpriteRenderer, direction * speed, spin,
                        pieceLifetime, damping, scale);
                }
            }

            Kill();
        }

        private static float NextFloat(System.Random random)
        {
            return (float)random.NextDouble();
        }

        private static float NextSignedFloat(System.Random random)
        {
            return NextFloat(random) * 2f - 1f;
        }

        public virtual void Kill()
        {
            if (IsDead)
            {
                return;
            }

            IsDead = true;
            if (Level != null && Level.HasObstacles)
            {
                Level.Pathfinder?.MarkObstacleLayerDirty();

                // SaveAndEnd owns teardown of the StaticObstacle instances that remain in
                // ObstacleMap.Obstacles. A destructible static obstacle must leave that list
                // before removal, otherwise teardown later accesses stale ownership.
                StaticObstacle staticObstacle = this as StaticObstacle;
                if (staticObstacle != null &&
                    Level.ObstacleMap != null && Level.ObstacleMap.Obstacles != null)
                {
                    Level.ObstacleMap.Obstacles.Remove(staticObstacle);
                }

                if (staticObstacle != null && staticObstacle.IsPooledStaticLayoutObstacle && Stage != null)
                {
                    StaticObstaclePool.GetOrCreate(Stage).ReleaseObstacle(staticObstacle);
                }
                else
                {
                    Destroy(gameObject);
                }
            }
            
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Obstacle x = obj as Obstacle;
            if (x == null)
            {
                return false;
            }

            return Id == x.Id;
        }

        public bool Equals(Obstacle other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(Obstacle a, Obstacle b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(Obstacle a, Obstacle b)
        {
            return !(a == b);
        }
    }
}