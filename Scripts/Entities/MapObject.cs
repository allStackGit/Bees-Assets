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

        InitializeSprite();
        //Debug.Log($"Setup {Name}");
    }

    protected virtual void InitializeSprite()
    {
        // Choose a random sprite.
        if (SpriteRenderer != null && Sprites != null && Sprites.Length > 0)
        {
            SpriteRenderer.sprite = Sprites[Utilities.RandomInt(Sprites.Length)];
        }
    }

    protected virtual void OnHealthChanged()
    {
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
            OnHealthChanged();

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
        if (System.Object.ReferenceEquals(this, obj))
        {
            return true;
        }

        // Preserve UnityEngine.Object's destroyed-object semantics. A destroyed
        // MonoBehaviour still has a managed wrapper but must behave as null.
        if ((UnityEngine.Object)this == null || obj == null)
        {
            return false;
        }

        _mapObject = obj as MapObject;
        if ((UnityEngine.Object)_mapObject == null)
        {
            return false;
        }

        // Id 0 means the object has not been registered with a Level yet. Distinct
        // prefab/runtime instances all begin with Id 0, so comparing those by Id
        // incorrectly collapses them into one logical object before Setup assigns
        // production IDs. Until both objects are initialized, only reference
        // equality (handled above) can establish identity.
        if (Id == 0 || _mapObject.Id == 0)
        {
            return false;
        }

        return Id == _mapObject.Id;
    }

    public bool Equals(MapObject other)
    {
        if (System.Object.ReferenceEquals(this, other))
        {
            return true;
        }
        if ((UnityEngine.Object)this == null || (UnityEngine.Object)other == null)
        {
            return false;
        }
        if (Id == 0 || other.Id == 0)
        {
            return false;
        }
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        // Before Setup, use Unity's per-instance hash so separate prefab/runtime
        // objects with the default Id do not collapse in HashSet/Distinct. After
        // registration, retain the existing stable game-identity hash behavior.
        return Id == 0 ? base.GetHashCode() : Id.GetHashCode();
    }

    public static bool operator ==(MapObject a, MapObject b)
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

        if (a.Id == 0 || b.Id == 0)
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
