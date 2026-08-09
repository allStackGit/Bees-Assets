using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Beacon : Ship
    {
        public Sprite StandardSprite, EnemySprite;
        private Sprite _originalStandardSprite, _originalEnemySprite;
        private ScaledTimer _beaconStatusTimer = new ScaledTimer();

        public void LookForShips()
        {
            if (IsUserControlled)
            {
                _beaconStatusTimer.Reuse(ConfigData.BeaconUpdateFrequency, SetBeaconStatus, true);
                Level.AddTimer(_beaconStatusTimer);
            }
        }

        public void SetBeaconStatus()
        {
            // Trigger exits normally maintain this set, but collider teardown can leave a
            // stale wrapper for a frame/lifecycle. Presentation should reflect live enemies,
            // not historical contact membership.
            ProximityCollider.NearbyEnemyShips.RemoveWhere(ship => ship == null || ship.IsDead);
            SpriteRenderer.sprite = ProximityCollider.NearbyEnemyShips.Count > 0
                ? EnemySprite
                : StandardSprite;
        }

        public override void SetColor()
        {
            // Beacon switches between two sprite fields at runtime, so resetting only the
            // renderer is insufficient when the pooled Beacon changes squads/colors.
            if (_originalStandardSprite == null)
            {
                _originalStandardSprite = StandardSprite;
                _originalEnemySprite = EnemySprite;
            }

            StandardSprite = _originalStandardSprite;
            EnemySprite = _originalEnemySprite;

            if (Squad.HasCustomColor)
            {
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                int[] changeablePixels = Utilities.GetChangablePixelsForImage(colors, StandardSprite);
                StandardSprite = Utilities.SetImageColor(Squad.Color, StandardSprite, changeablePixels);

                changeablePixels = Utilities.GetChangablePixelsForImage(colors, EnemySprite);
                EnemySprite = Utilities.SetImageColor(Squad.Color, EnemySprite, changeablePixels);
            }
            base.SetColor();
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            Level.CancelTimer(_beaconStatusTimer);
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
