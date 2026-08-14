using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private Color[] _colors;
        private Sprite _prefabSprite, _loadedSprite, _shipIcon, _recolored;
        private Vector2Int _setColorSize = Vector2Int.zero;
        private bool _hasLoadedSprite;
        private int[] _changablePixels;
        private float _healthPercent;
        private readonly ScaledTimer _showShipStatsTimer = new ScaledTimer();
        private readonly List<Sprite> _baseColorSprites = new List<Sprite>();

        private void EnsureBaseColorSprites()
        {
            if (_baseColorSprites.Count == OriginalColoredPrefabs.Count)
            {
                return;
            }

            _baseColorSprites.Clear();
            foreach (GameObject prefab in OriginalColoredPrefabs)
            {
                _baseColorSprites.Add(prefab.GetComponent<SpriteRenderer>().sprite);
            }
        }

        public virtual void SetColor()
        {
            EnsureBaseColorSprites();
            ColoredPrefabs = OriginalColoredPrefabs.ToList();
            OriginalSprites.Clear();
            OriginalSprites.AddRange(_baseColorSprites);

            if (Squad.HasCustomColor)
            {
                _colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                _tempIndex = 0;
                ColoredPrefabs.ForEach(prefab =>
                {
                    // Always recolor from the immutable prefab-era baseline. Pooled ships can
                    // move between squads/colors, so the currently displayed sprite may already
                    // be recolored from an earlier lifecycle.
                    _prefabSprite = _baseColorSprites[_tempIndex];
                    _setColorSize = new Vector2Int(_prefabSprite.texture.width, _prefabSprite.texture.height);
                    _hasLoadedSprite = false;

                    // The persisted HasCachedSprite flag can outlive the device-local PNG cache.
                    // Always probe the local cache for custom colors; a cache miss is repaired
                    // immediately from the authored sprite instead of trusting that remote flag.
                    _loadedSprite = FleetShip.LoadCachedSprite(
                        _tempIndex,
                        "ship",
                        _setColorSize,
                        Squad.SavedSquad.Color);
                    if (_loadedSprite != null)
                    {
                        prefab.GetComponent<SpriteRenderer>().sprite = _loadedSprite;
                        _hasLoadedSprite = true;
                    }

                    if (!_hasLoadedSprite)
                    {
                        _changablePixels = Utilities.GetChangablePixelsForImage(_colors, _prefabSprite);
                        _recolored = Utilities.SetImageColor(Squad.Color, _prefabSprite, _changablePixels);
                        prefab.GetComponent<SpriteRenderer>().sprite = _recolored;

                        // A different device may have the squad/color in server-backed save data
                        // without the generated PNG. Rebuild that local cache while the ship is
                        // already being recolored so subsequent loads use the normal fast path.
                        try
                        {
                            FleetShip.SaveSpriteToCache(
                                _tempIndex,
                                "ship",
                                _recolored.texture.GetPixels(),
                                _setColorSize,
                                Squad.SavedSquad.Color);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"Could not rebuild custom sprite cache for {FleetShip.Name}: {e.Message}");
                        }
                    }
                    _tempIndex++;
                });
            }
            else
            {
                _tempIndex = 0;
                ColoredPrefabs.ForEach(prefab =>
                {
                    prefab.GetComponent<SpriteRenderer>().sprite = _baseColorSprites[_tempIndex];
                    _tempIndex++;
                });
            }
        }

        public void SetSquadName()
        {
            Name = $"{Squad.Name}: {Name}";
            gameObject.name = Name;
        }

        public virtual void SetRocketFlares()
        {
            CenterRocketFlares.ForEach(flare => flare.SetActive(true));
            if (!HasRightRocketFlares || !HasLeftRocketFlares)
            {
                return;
            }

            if (_differenceInAngleToPoint > 5)
            {
                RightRocketFlares.ForEach(flare => flare.SetActive(true));
                LeftRocketFlares.ForEach(flare => flare.SetActive(false));
                AreRocketFlaresOutOfSync = true;
            }
            else if (_differenceInAngleToPoint < -5)
            {
                LeftRocketFlares.ForEach(flare => flare.SetActive(true));
                RightRocketFlares.ForEach(flare => flare.SetActive(false));
                AreRocketFlaresOutOfSync = true;
            }
            else if (!HasOnlySideRocketFlares)
            {
                RightRocketFlares.ForEach(flare => flare.SetActive(true));
                LeftRocketFlares.ForEach(flare => flare.SetActive(true));
                AreRocketFlaresOutOfSync = false;
            }
            else
            {
                RightRocketFlares.ForEach(flare => flare.SetActive(false));
                LeftRocketFlares.ForEach(flare => flare.SetActive(false));
            }
        }

        public void ShowWeaponRanges()
        {
            Turrets.ForEach(turret => turret.ShowRange());
        }

        public void HideWeaponRanges()
        {
            Turrets.ForEach(turret => turret.HideRange());
        }

        public void UpdateHealthBar()
        {
            if (!Level.Stage.IsRendering)
            {
                return;
            }

            _healthPercent = (float)Math.Round((double)Health / MaxHealth, 2);
            _healthBarFiller.localScale = new Vector2(_healthPercent, _healthBarFiller.localScale.y);
            if (_healthPercent > .5f) _healthBarFillerSprite.color = ConfigData.GetUIColor("good");
            else if (_healthPercent > .25f) _healthBarFillerSprite.color = ConfigData.GetUIColor("medium");
            else _healthBarFillerSprite.color = ConfigData.GetUIColor("bad");
        }

        protected virtual void DropExplosionAnimation()
        {
            if (Stage.IsTraining)
            {
                return;
            }

            ShipExplosion.transform.parent = Level.Map.Transform;
            ShipExplosion.transform.localPosition = GetPosition();
            ShipExplosion.transform.eulerAngles = Vector3.forward * Rotation;
            ShipExplosion.SetActive(true);
            if (Level.Stage.ActivateAudio && HasShipExplosionSoundEffect)
            {
                ShipExplosionSoundEffect.Play();
            }
            if (HasRemainsShip)
            {
                ShipRemains.Place();
            }
        }

        public void ShowShipStats()
        {
            Stage.Menus.ShowShipStats(FleetShip);
        }

        private void OnMouseEnter()
        {
            if (!ConfigData.SpawnedOnlyShipTypes.Contains(ShipType) && !Stage.IsTraining && ShipType != ConfigData.ShipTypes.HumanTarget)
            {
                _showShipStatsTimer.Reuse(1, ShowShipStats);
                Level.AddTimer(_showShipStatsTimer);
            }
        }

        private void OnMouseExit()
        {
            Level.CancelTimer(_showShipStatsTimer);
            if (!Stage.IsTraining)
            {
                Stage.Menus.ShipInfoBox.SetActive(false);
            }
        }
    }
}
