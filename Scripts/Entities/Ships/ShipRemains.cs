using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipRemains : MonoBehaviour
    {
        public Ship Ship;
        /// <summary>
        /// Controls the animation and recoloring of sprites if the ship has ship remains
        /// </summary>
        public RemainsAnimationController AnimationController;
        public bool HasAnimationController;
        public void Create(Ship ship)
        {
            Ship = ship;
            gameObject.SetActive(false);
            AnimationController = GetComponent<RemainsAnimationController>();
            if (AnimationController != null)
            {
                AnimationController.Ship = Ship;
                HasAnimationController = true;
            }
        }
        public void Setup()
        {
            name = $"Remains - {Ship.Name}"; // [debug] not necessary for anything else
            transform.parent = Ship.Level.Map.transform;
            if (HasAnimationController && Ship.Squad.HasCustomColor)
            {
                AnimationController.RecolorAnimationSprites();
            }

        }
        public void Place()
        {
            transform.localPosition = Ship.GetPosition();
            transform.eulerAngles = Ship.transform.eulerAngles;
            gameObject.SetActive(true);
            Ship.Level.State.AddDeadBody(this);

            if (Ship.Squad.HasCustomColor && AnimationController == null)
            {
                Color[] colors = ConfigData.ChangeableShipColors.GetValueOrDefault(Ship.ShipType);
                Sprite prefabSprite = GetComponent<SpriteRenderer>().sprite;
                Sprite shipIcon = prefabSprite;
                int[] changeablePixels = Utilities.SetChangablePixelsForImage(colors, shipIcon);
                Sprite recolored = Utilities.SetImageColor(Ship.Squad.Color, shipIcon, changeablePixels);
                GetComponent<SpriteRenderer>().sprite = recolored;
            }

            Invoke(nameof(Kill), 5);
        }

        public void Kill()
        {
            //Destroy(gameObject);
            gameObject.SetActive(false);
        }
    }
}