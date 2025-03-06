using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
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
        private ScaledTimer _killTimer = new ScaledTimer();
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
        }
        public void Setup()
        {
            name = $"Remains - {Ship.Name}"; // [debug] not necessary for anything else
            Transform.parent = Ship.Level.Map.transform;
            if (HasAnimationController && Ship.Squad.HasCustomColor)
            {
                AnimationController.RecolorAnimationSprites();
            }

        }
        public void Place()
        {


            if (!Ship.IsLastShipOnSide())
            {
                Transform.localPosition = Ship.GetPosition();
                Transform.eulerAngles = Vector3.forward * Ship.Rotation;
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

                _killTimer.Reuse(5, Kill);
                Ship.Level.AddTimer(_killTimer);
            }


            //Invoke(nameof(Kill), 5);
        }

        public void Kill()
        {
            gameObject.SetActive(false);
        }
    }
}