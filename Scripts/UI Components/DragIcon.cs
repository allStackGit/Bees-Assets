

using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;


namespace Assets.Scripts.UIComponents
{
    public class DragIcon
    {
        private const string CarrierDeckVariantObjectName = "Carrier Deck Variant";

        public Vector2 Position => _icon.transform.position;
        public int Id;
        private GameObject _icon;
        private GameObject _deadShipBox = null;
        private FleetShip _fleetShip;
        private SquadMaker _scene;
        private int[] _changeablePixels;
        private Image _carrierDeckVariantImage;

        public bool HasDeadShipBox => _deadShipBox != null;
        public DragIcon(SquadMaker scene, GameObject icon, FleetShip fleetShip, string gameObjectName, int id)
        {
            _scene = scene;
            _icon = icon;
            _fleetShip = fleetShip;
            icon.name = gameObjectName;
            Id = id;
            if (fleetShip.Side == ConfigData.Configuration.HumanSide)
            {
                SetChangablePixels(ConfigData.ChangeableShipColors.GetValueOrDefault(fleetShip.Type));
            }
            //Debugger.PrintList(_changeablePixels.ToList());
        }

        public FleetShip GetFleetShip()
        {
            return _fleetShip;
        }
        public GameObject GetIcon() { 
            return _icon;
        }
        public GameObject GetDeadShipBox()
        {
            return _deadShipBox;
        }
        //public DragIconDropper GetDropper()
        //{
        //    return _dropper;
        //}

        public void SetPosition(Vector2 position)
        {
            _icon.transform.position = position;
        }
        public void SetChangablePixels(Color[] colors)
        {
            //Debug.Log($"Setting changable pixels for {_icon.name}");
            Image image = _icon.GetComponent<Image>();
            _changeablePixels = Utilities.GetChangablePixelsForImage(colors, image.sprite);
        }
        public void SetColor(Color color)
        {
            if (color.Equals(ConfigData.UnsetColor))
            {
                SetCarrierDeckVariant(null);
                return;
            }
            Image image = _icon.GetComponent<Image>();

            image.sprite = Utilities.SetImageColor(color, image.sprite, _changeablePixels);

            if (_fleetShip != null && _fleetShip.Type == ConfigData.ShipTypes.Carrier)
            {
                SetCarrierDeckVariant(CarrierDeckVariants.GetDeckSprite(color));
            }
        }

        private void SetCarrierDeckVariant(Sprite deckSprite)
        {
            if (_fleetShip == null || _fleetShip.Type != ConfigData.ShipTypes.Carrier)
            {
                return;
            }

            if (deckSprite == null)
            {
                if (_carrierDeckVariantImage != null)
                {
                    _carrierDeckVariantImage.enabled = false;
                }
                return;
            }

            EnsureCarrierDeckVariantImage();
            _carrierDeckVariantImage.sprite = deckSprite;
            _carrierDeckVariantImage.color = Color.white;
            _carrierDeckVariantImage.enabled = true;
        }

        private void EnsureCarrierDeckVariantImage()
        {
            if (_carrierDeckVariantImage != null)
            {
                return;
            }

            Transform existing = _icon.transform.Find(CarrierDeckVariantObjectName);
            if (existing != null)
            {
                _carrierDeckVariantImage = existing.GetComponent<Image>();
            }

            if (_carrierDeckVariantImage == null)
            {
                GameObject deckObject = new GameObject(
                    CarrierDeckVariantObjectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                deckObject.transform.SetParent(_icon.transform, false);
                _carrierDeckVariantImage = deckObject.GetComponent<Image>();
            }

            RectTransform rectTransform = _carrierDeckVariantImage.rectTransform;
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.localScale = Vector3.one;
            _carrierDeckVariantImage.raycastTarget = false;
            _carrierDeckVariantImage.preserveAspect = false;
        }

        public void SetDeadShipBox(GameObject deadShipBox)
        {
            _deadShipBox = deadShipBox;
        }

        public void Reposition(Vector2 position, SquadShip ship)
        {
            Dropper dropper = _scene.GetDropper();
            dropper.SetCurrentDragIcon(this);
            dropper.PlaceShipAtPosition(position, ship);
            _scene.FleetDragEnd();

        }




        public void RemoveDragIcon()
        {
            SavedSquad currentSquad = _scene.GetCurrentSquad();
            FleetShip fleetShip = GetFleetShip();
            if (currentSquad != null)
            {
                //Debug.Log($"Removing drag icon from {currentSquad}");
                SquadShip squadShip = currentSquad.GetShip(fleetShip.Id);
                if (squadShip != null)
                {
                    //Debug.Log($"Removing squad ship from {currentSquad}");
                    currentSquad.RemoveShipFromSquad(squadShip, true);
                    _scene.UpdateShipCounter(fleetShip.Type);
                }
            }

            _scene.GetDropper().RemoveDragIcon(this);
        }
        public bool Equals(DragIcon dragIcon)
        {
            return dragIcon.Id == Id;
        }
        
    }
}