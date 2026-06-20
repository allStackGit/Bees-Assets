using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

public class MapObject : MonoBehaviour
{
    // Static objects in the game that collide with projectiles

    public Rigidbody2D Body;
    public Collider2D Collider;
    public int Id;
    public int MaxHealth, Health;
    public SpriteRenderer SpriteRenderer;
    public Sprite[] Sprites;
    public Level Level;
    public string Name;
    public bool IsDead;
    public Projectile LastHitProjectile;

    public virtual void Setup(Level level)
    {

        Level = level;
        Id = Level.State.GetId();
        Name = $"{Name} #{Id}";
        gameObject.name = Name;
        Health = MaxHealth;

        // Choose a random sprite 
        SpriteRenderer.sprite = Sprites[Utilities.RandomInt(Sprites.Length)];
        Debug.Log($"Setup {Name}");
    }

    public void OnTriggerEnter2D(Collider2D collider)
    {
        // Only care about projectile collisions here
        GameObject colliding = collider.gameObject;
        if (colliding.CompareTag("Projectile"))
        {
            LastHitProjectile = colliding.GetComponent<Projectile>();
            // Subtract projectile power from health
            Health -= LastHitProjectile.Power;

            // If health is depleted, kill the object
            if (Health <= 0)
            {
                Kill();
            }
            // Destroy the projectile (run its kill sequence so explosions play)
            LastHitProjectile.KillSequence();
        }
    }

    public virtual void Kill()
    {
        if (!IsDead)
        {
            IsDead = true;
            Destroy(gameObject);
        }
    }
    private MapObject _mapObject;
    public override bool Equals(System.Object obj)
    {
        if (obj == null)
        {
            return false;
        }

        // If parameter cannot be cast to class return false.
        _mapObject = obj as MapObject;
        if (_mapObject == null)
        {
            return false;
        }

        return Id == _mapObject.Id;
    }

    public bool Equals(MapObject other)
    {
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(MapObject a, MapObject b)
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

    public static bool operator !=(MapObject a, MapObject b)
    {
        return !(a == b);
    }
}
