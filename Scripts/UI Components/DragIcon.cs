

using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;


namespace Assets.Scripts.UIComponents
{
    public class DragIcon
    {
        public Vector2 Position => _icon.transform.position;
        public int Id;
        private GameObject _icon;
        private GameObject _deadShipBox = null;
        private FleetShip _fleetShip;
        private SquadMaker _scene;
        private int[] _changeablePixels;

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
            UnityEngine.UI.Image image = _icon.GetComponent<UnityEngine.UI.Image>();
            _changeablePixels = Utilities.GetChangablePixelsForImage(colors, image.sprite);
        }
        public void SetColor(Color color)
        {
            if (color.Equals(ConfigData.UnsetColor))
            {
                return;
            }
            UnityEngine.UI.Image image = _icon.GetComponent<UnityEngine.UI.Image>();

            image.sprite = Utilities.SetImageColor(color, image.sprite, _changeablePixels);
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
                Debug.Log($"Removing drag icon from {currentSquad}");
                SquadShip squadShip = currentSquad.GetShip(fleetShip.Id);
                if (squadShip != null)
                {
                    Debug.Log($"Removing squad ship from {currentSquad}");
                    currentSquad.RemoveShipFromSquad(squadShip);
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