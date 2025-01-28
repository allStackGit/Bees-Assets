using Assets.Scripts.Levels;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Beacon : Ship
    {
        public Sprite StandardSprite, EnemySprite;
        public SpriteRenderer SpriteRenderer;

        public void LookForShips()
        {
            if (!Stage.IsTraining && IsUserControlled)
            {
                InvokeRepeating(nameof(SetBeaconStatus), ConfigData.BeaconUpdateFrequency, ConfigData.BeaconUpdateFrequency);
            }
        }
        public void SetBeaconStatus()
        {
            if (ProximityCollider.NearbyEnemyShips.Count > 0)
            {
                SpriteRenderer.sprite = EnemySprite;
            }
            else
            {
                SpriteRenderer.sprite = StandardSprite;
            }
        }

        public override void SetColor()
        {
            if (Squad.HasCustomColor)
            {
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(ShipType);
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, StandardSprite);
                StandardSprite = Utilities.SetImageColor(Squad.Color, StandardSprite, changeablePixels);

                changeablePixels = Utilities.SetChangablePixelsForImage(colors, EnemySprite);
                EnemySprite = Utilities.SetImageColor(Squad.Color, EnemySprite, changeablePixels);
            }
            base.SetColor();
        }
    }
}