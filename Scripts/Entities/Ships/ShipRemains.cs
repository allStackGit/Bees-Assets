using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipRemains : MonoBehaviour
    {
        public Ship Ship;
        public Transform Transform;
        /// <summary>
        /// Controls the animation and recoloring of sprites if the ship has ship remains
        /// </summary>
        public RemainsAnimationController AnimationController;
        public bool HasAnimationController;

        private readonly ScaledTimer _killTimer = new ScaledTimer();
        private Level _ownerLevel;
        private SpriteRenderer _spriteRenderer;
        private Sprite _baseSprite;

        public void Create(Ship ship)
        {
            Ship = ship;
            Transform = transform;
            gameObject.SetActive(false);
            AnimationController = GetComponent<RemainsAnimationController>();
            if (AnimationController != null)
            {
                AnimationController.Ship = Ship;
                HasAnimationController = true;
            }
            else
            {
                _spriteRenderer = GetComponent<SpriteRenderer>();
                _baseSprite = _spriteRenderer != null ? _spriteRenderer.sprite : null;
            }
        }

        public void Setup()
        {
            // The remains object is permanently paired with a pooled Ship wrapper. Retire any
            // corpse still owned by the wrapper's previous lifecycle before adopting a new Level;
            // otherwise the same ScaledTimer can be registered with two Levels at once.
            RetirePreviousPlacement();
            _ownerLevel = Ship.Level;
            Transform.parent = _ownerLevel.Map.Transform;

            if (HasAnimationController && Ship.Squad.HasCustomColor)
            {
                AnimationController.RecolorAnimationSprites();
            }
            else if (!HasAnimationController && _spriteRenderer != null)
            {
                _spriteRenderer.sprite = _baseSprite;
            }
        }

        public void Place()
        {
            if (_ownerLevel == null)
            {
                _ownerLevel = Ship.Level;
            }

            Transform.localPosition = Ship.GetPosition();
            Transform.eulerAngles = Vector3.forward * Ship.Rotation;
            gameObject.SetActive(true);
            if (!_ownerLevel.State.Deadbodies.Contains(this))
            {
                _ownerLevel.State.AddDeadBody(this);
            }

            // Recolor from the immutable prefab-era sprite. The remains object is reused with
            // its pooled Ship and may belong to a differently colored squad next lifecycle.
            if (Ship.Squad.HasCustomColor && !HasAnimationController && _spriteRenderer != null && _baseSprite != null)
            {
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(Ship.ShipType);
                int[] changeablePixels = Utilities.GetChangablePixelsForImage(colors, _baseSprite);
                _spriteRenderer.sprite = Utilities.SetImageColor(Ship.Squad.Color, _baseSprite, changeablePixels);
            }

            _killTimer.Reuse(5, Kill);
            _ownerLevel.AddTimer(_killTimer);
        }

        public void Kill()
        {
            RetirePreviousPlacement();
        }

        private void RetirePreviousPlacement()
        {
            if (_ownerLevel != null)
            {
                _ownerLevel.CancelTimer(_killTimer);
                _ownerLevel.State.Deadbodies.Remove(this);
            }
            gameObject.SetActive(false);
            _ownerLevel = null;
        }
    }
}
