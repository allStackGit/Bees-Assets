using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UIElements;

namespace Assets.Scripts.Level
{
    public class Selector : MonoBehaviour
    {
        private List<Ship> potentiallySelectedShips = new List<Ship>();
        private GameObject box;
        public LevelStage Level;
        public void Setup(LevelStage level, GameObject box)
        {
            Level = level;
            this.box = box;
        }
        public void SelectShip(Ship ship)
        {
            potentiallySelectedShips.Add(ship);
        }
        public void DeselectShip(Ship ship)
        {
            potentiallySelectedShips.Remove(ship);
        }
        public void ClearSelectedShips()
        {
            potentiallySelectedShips.Clear();
        }
        public void DrawSelectionBox(Vector2 startPosition, Vector2 endPosition)
        {

            //Debug.Log($"Making a selection between {_mouseDownPosition} and {_mousePosition}");

            // calculate width and height of box
            float width = Math.Abs(startPosition.x - endPosition.x);
            float height = Math.Abs(startPosition.y - endPosition.y);

            // calculate center point of box

            float midX, midY;
            if (endPosition.x > startPosition.x)
            {
                midX = startPosition.x + (width / 2);
            }
            else
            {
                midX = startPosition.x - (width / 2);
            }
            if (endPosition.y > startPosition.y)
            {
                midY = startPosition.y + (height / 2);
            }
            else
            {
                midY = startPosition.y - (height / 2);
            }


            // move and activate SelectionBox
            box.SetActive(true);
            box.transform.position = new Vector3(midX, midY, 0);
            box.transform.localScale = new Vector3(width, height, 0);

            //Debug.Log($"Activated box at {SelectionBox.transform.position} with size {SelectionBox.transform.localScale}");
        }
        public void Deactivate()
        {
            box.SetActive(false);
        }
        public void SelectShipsInBox()
        {
            List<Squad> squads = new List<Squad>();
            potentiallySelectedShips.ForEach((ship) =>
            {
                if (!squads.Contains(ship.Squad))
                {
                    squads.Add(ship.Squad);
                    //Debug.Log($"Selecting #{ship.Squad.SquadNumber} squad");
                }
            });
            Level.GetState().SelectSquads(squads);
            ClearSelectedShips();
        }
    }
}