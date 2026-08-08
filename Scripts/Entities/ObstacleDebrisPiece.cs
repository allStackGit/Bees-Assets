using UnityEngine;

namespace Assets.Scripts.Entities
{
    /// <summary>
    /// Cosmetic fragment spawned when an obstacle breaks apart. It deliberately has no collider
    /// or Rigidbody2D so debris cannot affect combat, pathfinding, or physics.
    /// </summary>
    public class ObstacleDebrisPiece : MonoBehaviour
    {
        private SpriteRenderer _spriteRenderer;
        private Vector2 _velocity;
        private float _angularVelocity;
        private float _lifetime;
        private float _age;
        private float _damping;
        private Color _baseColor;

        public void Setup(Sprite sprite, SpriteRenderer sourceRenderer, Vector2 velocity,
            float angularVelocity, float lifetime, float damping, float scale)
        {
            _velocity = velocity;
            _angularVelocity = angularVelocity;
            _lifetime = Mathf.Max(0.01f, lifetime);
            _damping = Mathf.Max(0f, damping);
            transform.localScale = Vector3.one * scale;

            // scrap_bits sprites currently use a lower-left pivot. Put the renderer on a child
            // and offset it so the visible fragment rotates around its center instead.
            GameObject spriteObject = new GameObject("Sprite");
            spriteObject.transform.SetParent(transform, false);
            spriteObject.transform.localPosition = -sprite.bounds.center;

            _spriteRenderer = spriteObject.AddComponent<SpriteRenderer>();
            _spriteRenderer.sprite = sprite;

            if (sourceRenderer != null)
            {
                _spriteRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
                _spriteRenderer.sortingOrder = sourceRenderer.sortingOrder + 1;
                _baseColor = sourceRenderer.color;
            }
            else
            {
                _baseColor = Color.white;
            }

            _spriteRenderer.color = _baseColor;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            _age += deltaTime;

            transform.position += (Vector3)(_velocity * deltaTime);
            transform.Rotate(0f, 0f, _angularVelocity * deltaTime);

            if (_damping > 0f)
            {
                _velocity *= Mathf.Exp(-_damping * deltaTime);
            }

            float fadeStart = _lifetime * 0.6f;
            if (_spriteRenderer != null && _age > fadeStart)
            {
                float alpha = 1f - Mathf.InverseLerp(fadeStart, _lifetime, _age);
                Color color = _baseColor;
                color.a *= alpha;
                _spriteRenderer.color = color;
            }

            if (_age >= _lifetime)
            {
                Destroy(gameObject);
            }
        }
    }
}
