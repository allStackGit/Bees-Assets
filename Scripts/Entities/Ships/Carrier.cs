using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Carrier : Ship
    {
        private const string DeckVariantObjectName = "Carrier Deck Variant";
        private SpriteRenderer _deckVariantRenderer;

        public override void SetColor()
        {
            base.SetColor();

            if (!Stage.IsRendering || Squad == null || !Squad.HasCustomColor)
            {
                SetDeckVariantVisible(false);
                return;
            }

            Sprite deckSprite = CarrierDeckVariants.GetDeckSprite(Squad.Color);
            if (deckSprite == null)
            {
                SetDeckVariantVisible(false);
                return;
            }

            SpriteRenderer baseRenderer = GetComponent<SpriteRenderer>();
            if (baseRenderer == null || baseRenderer.sprite == null)
            {
                SetDeckVariantVisible(false);
                return;
            }

            EnsureDeckVariantRenderer(baseRenderer);
            _deckVariantRenderer.sprite = deckSprite;
            _deckVariantRenderer.sortingLayerID = baseRenderer.sortingLayerID;
            _deckVariantRenderer.sortingOrder = baseRenderer.sortingOrder + 1;
            _deckVariantRenderer.flipX = baseRenderer.flipX;
            _deckVariantRenderer.flipY = baseRenderer.flipY;

            Vector2 baseSize = baseRenderer.sprite.bounds.size;
            Vector2 deckSize = deckSprite.bounds.size;
            if (deckSize.x > 0f && deckSize.y > 0f)
            {
                _deckVariantRenderer.transform.localScale = new Vector3(
                    baseSize.x / deckSize.x,
                    baseSize.y / deckSize.y,
                    1f);
            }

            SetDeckVariantVisible(true);
        }

        private void EnsureDeckVariantRenderer(SpriteRenderer baseRenderer)
        {
            if (_deckVariantRenderer != null)
            {
                return;
            }

            Transform existing = transform.Find(DeckVariantObjectName);
            if (existing != null)
            {
                _deckVariantRenderer = existing.GetComponent<SpriteRenderer>();
            }

            if (_deckVariantRenderer == null)
            {
                GameObject deckObject = new GameObject(DeckVariantObjectName);
                deckObject.transform.SetParent(transform, false);
                _deckVariantRenderer = deckObject.AddComponent<SpriteRenderer>();
            }

            Transform deckTransform = _deckVariantRenderer.transform;
            deckTransform.localPosition = Vector3.zero;
            deckTransform.localRotation = Quaternion.identity;
            _deckVariantRenderer.color = Color.white;
            _deckVariantRenderer.sharedMaterial = baseRenderer.sharedMaterial;
        }

        private void SetDeckVariantVisible(bool visible)
        {
            if (_deckVariantRenderer != null)
            {
                _deckVariantRenderer.enabled = visible;
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (!IsDead)
            {
                List<Ship> levelShips = Level.State.Ships;
                Carrier replacementCarrier = null;
                for (int i = 0; i < levelShips.Count; i++)
                {
                    Ship candidate = levelShips[i];
                    if (candidate.Side == Side && candidate is Carrier carrier && carrier != this && !carrier.IsDead)
                    {
                        replacementCarrier = carrier;
                        break;
                    }
                }

                for (int i = 0; i < levelShips.Count; i++)
                {
                    Ship candidate = levelShips[i];
                    if (candidate.Side != Side || !(candidate is CarrierShip carrierShip) || carrierShip.Carrier != this)
                    {
                        continue;
                    }

                    if (replacementCarrier != null)
                    {
                        carrierShip.Carrier = replacementCarrier;
                    }
                    else
                    {
                        if (carrierShip is Striker striker)
                        {
                            striker.LastCarrierPosition = GetPosition();
                        }
                        carrierShip.Carrier = null;
                    }

                    if (carrierShip.Squad is CarrierSquad carrierSquad)
                    {
                        carrierSquad.Carrier = replacementCarrier;
                    }
                }
            }

            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
