

using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Assets.Scripts.UIComponents
{
    public class Dropper
    {
        //private DragIcon _dragIcon;
        private SquadMaker _scene;
        private bool _isAutoPlacing, _isDragging;
        private List<DragIcon> _dragIcons = new List<DragIcon>();
        private DragIcon _currentDragIcon;
        private int _dragIconCount = 0;
        public bool IsValidDropLocation;


        public bool IsDragging => _isDragging;
        public SavedSquad CurrentSquad => _scene.GetCurrentSquad();
        public List<FleetShip> FleetList => _scene.GetFleetList();
        public bool HasCurrentSquad => CurrentSquad != null;

        public Dropper(SquadMaker scene)
        {
            //_dragIcon = dragIcon;
            _scene = scene;
        }

        // Get and set methods
        public List<DragIcon> GetDragIcons()
        {
            return _dragIcons;
        }
        public DragIcon GetCurrentDragIcon()
        {
            return _currentDragIcon;
        }
        public void SetCurrentDragIcon(DragIcon dragIcon)
        {
            _currentDragIcon = dragIcon;
        }


        // Drag icon flow
        public void PullNewDragIcon(ConfigData.ShipTypes shipType)
        {
            FleetShip fleetShip = FleetList.Where((s) => s.Type == shipType).FirstOrDefault();

            // if you've got a valid ship to drag, you're not already dragging, and the squad hasn't hit it's max size
            if (fleetShip != null && !_isDragging && (!HasCurrentSquad || !CurrentSquad.HasMaxShips))
            {
                MakeDragIcon(fleetShip);
                FleetList.Remove(_currentDragIcon.GetFleetShip());
            }
            else
            {
                Debug.Log($"Couldn't make new drag icon for {shipType}, {fleetShip}, {!_isDragging}, {CurrentSquad}");
                if (HasCurrentSquad && CurrentSquad.HasMaxShips)
                {
                    Utilities.SetBadColor(_scene.SquadShipCount);
                }
            }
        }
        public void MakeDragIcon(FleetShip fleetShip)
        {
            GameObject dragIconPrefab = _scene.GetDragIconPrefab(fleetShip.Type);
            //Vector2 shipSize = ConfigData.ShipSizes.GetValueOrDefault(fleetShip.Type);
            //Vector2 size = new Vector2(ConfigData.DragIconSize.y * (shipSize.x / shipSize.y), ConfigData.DragIconSize.y);
            string name = $"{fleetShip.Name} #{fleetShip.Id}";

            GameObject dragIcon = GameObject.Instantiate(dragIconPrefab);
            UnityEngine.UI.Image image = dragIcon.GetComponent<UnityEngine.UI.Image>();
            dragIcon.transform.SetParent(dragIconPrefab.transform.parent, false);
            image.SetNativeSize();
            dragIcon.transform.localScale = ConfigData.BaseDragIconSize / ConfigData.GetShipSizeFactor(fleetShip.Type);


            DragIcon newDragIcon = new DragIcon(_scene, dragIcon, fleetShip, name, _dragIconCount++);
            _dragIcons.Add(newDragIcon);
            _currentDragIcon = newDragIcon;
        }
        public void SetupActiveDragging(Vector2 position, bool isAutoPlacing)
        {
            if (_currentDragIcon != null)
            {
                _isAutoPlacing = isAutoPlacing;
                _isDragging = true;
                IsValidDropLocation = false;
                //Vector2 size = _currentDragIcon.GetIcon().GetComponent<RectTransform>().sizeDelta;
                //Debug.Log($"sizeDelta for current drag icon: {size}");


                // Get the drag icon and position it
                GameObject icon = _currentDragIcon.GetIcon();
                _currentDragIcon.SetPosition(position);
                icon.SetActive(true);

                // Set scene components
                _scene.DragStatusBox.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
                _scene.DragStatusBox.transform.position = _currentDragIcon.Position;
                _scene.DragStatusBox.GetComponent<RectTransform>().sizeDelta = ConfigData.ShipOffset;
                _scene.DragStatusBox.transform.localScale = new Vector2(4, 4);

                //_scene.DragStatusBox.GetComponent<RectTransform>().sizeDelta = size;
                //_scene.DragStatusBox.transform.localScale = new Vector3(.30f, .30f, 0) / ConfigData.GetShipSizeFactor(_currentDragIcon.GetFleetShip().Type);
                _scene.DragStatusBox.SetActive(true);
                _scene.DropBox.SetActive(true);
                _scene.UpdateShipCounter(_currentDragIcon.GetFleetShip());
                _scene.DelayedHideShipStats();

            }

        }
        public bool PlaceShipAtPosition(Vector2 position, SquadShip ship)
        {
            _isAutoPlacing = true;
            _currentDragIcon.SetPosition(position);
            _scene.DragStatusBox.transform.position = _currentDragIcon.Position;
            IsValidDropLocation = CheckValidDropLocation(position, false, ship, _currentDragIcon.GetFleetShip().Type);
            return IsValidDropLocation;
        }
        public void AutoPlaceShip(ConfigData.ShipTypes shipType)
        {
            if (!_isDragging)
            {
                PullNewDragIcon(shipType);
                Vector2 position = _scene.DropBox.transform.position;
                SetupActiveDragging(position, true);

                IsValidDropLocation = true;
                _scene.FleetDragEnd();
                _scene.SetFormation(_scene.CurrentFormation);
            }
        }
        public void StartDragExistingIcon(FleetShip fleetShip)
        {
            _currentDragIcon = GetDragIcons().Find((d) => d.GetFleetShip() == fleetShip);
        }
        public void DraggingNewIcon()
        {
            //Debug.Log($"Dragging {_currentDragIcon.Icon.name}");
            if (_currentDragIcon != null)
            {
                _isDragging = true;
                Vector2 position = MouseDragPosition();
                GameObject deadShipBox = _currentDragIcon.GetDeadShipBox();

                _currentDragIcon.SetPosition(position);
                _scene.DragStatusBox.transform.position = position;
                if (deadShipBox != null)
                {
                    deadShipBox.transform.position = position;
                }

                if (CheckValidDropLocation(position, true, null, _currentDragIcon.GetFleetShip().Type))
                {
                    Utilities.SetGoodColor(_scene.DragStatusBox);
                    IsValidDropLocation = true;
                }
                else
                {
                    Utilities.SetBadColor(_scene.DragStatusBox);
                    IsValidDropLocation = false;

                }
            }
        }
        public void EndDragging()
        {
            //Debug.Log($"Stopped dragging {_dragIcon.GetFleetShip().Type}");
            if (_currentDragIcon != null)
            {
                if (IsValidDropLocation)
                {
                    //Debug.Log("Valid drop location, didn't destroy");
                    //Debug.Log($"Dropped {_currentDragIcon.FleetShip.Type} -----------------------------");
                    Color squadColor = _scene.GetSquadColor();
                    Vector2 position = _currentDragIcon.Position;
                    FleetShip fleetShip = _currentDragIcon.GetFleetShip();
                    Vector2 worldPointPosition = _scene.Camera.ScreenToWorldPoint(position);

                    //Debug.Log($"World point position of dropped ship: {worldPointPosition}");

                    _currentDragIcon.SetColor(squadColor);
                    Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("squad-ship-counter"));


                    if (fleetShip.IsDead && !_currentDragIcon.HasDeadShipBox)
                    {
                        // instantiate a dead ship box and place it over the drag icon
                        GameObject deadShipBox = MakeDeadShipBox(fleetShip);
                        deadShipBox.transform.position = position;
                        _currentDragIcon.SetDeadShipBox(deadShipBox);
                    }

                    if (HasCurrentSquad)
                    {
                        if (!CurrentSquad.HasShip(fleetShip))
                        {
                            CurrentSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, worldPointPosition));
                        }
                        else
                        {
                            //currentSquad.GetShip(fleetShip.Id).SetOffset(position);
                            CurrentSquad.GetShip(fleetShip.Id).SetOffset(worldPointPosition);
                        }
                        CurrentSquad.SetChanged(true);

                    }
                    else
                    {

                        //Debug.Log($"Ship transform position {_currentDragIcon.transform.position}, ship position in box {positionWithinBox}");
                        _scene.SetCurentSquad(new SavedSquad(-1, _scene.Side, _scene.GetSquadName(), worldPointPosition, 
                            false, false, ConfigData.DefaultShootingStrategy, squadColor));

                        CurrentSquad.SetChanged(true);
                        CurrentSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, worldPointPosition));

                        if (_scene.HasActionBox)
                        {
                            _scene.ActionBox.SetupForSquad();
                        }

                    }
                    
                }
                else
                {
                    Debug.Log("INVALID PLACEMENT. REMOVING DRAG ICON");
                    //_currentDragIcon.Icon.SetActive(false);
                    _currentDragIcon.RemoveDragIcon();
                }
            }
            ResetDrag();

        }


        // Set formations
        public void LineFormation()
        {
            MultiLine(ConfigData.Configuration.MaxSquadWidth, ConfigData.Configuration.MaxSquadHeight);
        }
        public void MultiLine(int maxWidth, int maxLines, bool hollow = false)
        {
            List<DragIcon> validDragIcons = GetDragIcons();
            List<DragIcon> dropped = new List<DragIcon>();
            for (int row = 0; row < maxLines && validDragIcons.Count > 0; row++)
            {
                LineMaker(maxWidth, row + 1, validDragIcons.GetRange(0, Math.Clamp(maxWidth, 0, validDragIcons.Count)), hollow).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();
            }
        }
        public List<DragIcon> LineMaker(int maxWidth, int level, List<DragIcon> dragIcons, bool hollow = false)
        {
            if (dragIcons == null)
            {
                dragIcons = GetDragIcons();
            }
            level = Math.Clamp(level - 1, 0, ConfigData.Configuration.MaxSquadHeight);
            maxWidth = Math.Clamp(maxWidth, 0, ConfigData.Configuration.MaxSquadWidth);
            List<DragIcon> dropped = new List<DragIcon>();
            //Debug.Log($"Making a line formation");
            // loop through all the drag icons out there
            for (int ships = 0; ships < maxWidth && ships < dragIcons.Count; ships++)
            {
                // for each drag icon, determine its position based off of its place in the order
                DragIcon dragIcon = dragIcons.GetRange(ships, 1).First();

                //Vector2 screenPoint = Camera.WorldToScreenPoint(ConfigData.ShipOffset);
                //Vector2 change = new Vector2(Mathf.Abs(BaseWorldPoint.x - screenPoint.x), Mathf.Abs(BaseWorldPoint.y - screenPoint.y));

                Vector2 change = Utilities.WorldUnitsToScreenPixels(ConfigData.ShipOffset, _scene.Camera) * 1.05f;

                //Debug.Log($"Ship offset world units for auto placing: {ConfigData.ShipOffset}, screen pixels {change}");

                //Debug.Log($"change: {change}");

                float xIncrement = change.x;
                float yIncrement = change.y;


                Vector2 position = new Vector2(_scene.DropBox.transform.position.x+30, _scene.DropBox.transform.position.y + ConfigData.OffsetFromCenterOfSquadMakerDropBox);
                //Debug.Log($"Position after auto dropping {position}");
                float movement;
                float movementDown = level * yIncrement;
                int steps = ships;

                if (steps % 2 == 0)
                {
                    int sideSteps = (int)Math.Floor((double)steps / 2);
                    movement = sideSteps * xIncrement;
                }
                else
                {
                    int sideSteps = (int)Math.Ceiling((double)steps / 2);
                    movement = -1 * sideSteps * xIncrement;
                }
                int sideCheck = maxWidth;
                if (!hollow || maxWidth < 3 || ships == sideCheck - 2 || ships == sideCheck - 1)
                {
                    //Debug.Log($"Placing the ship because it's either not hollow ({hollow}) or the maxWidth is less than 3 ({maxWidth}) or the shipIndex" +
                    //    $"is equal to {sideCheck - 2} or {sideCheck - 1} ({ships})");
                    Vector2 movedPosition = new Vector2(position.x + movement, position.y - movementDown);

                    dragIcon.Reposition(movedPosition, null);
                    dropped.Add(dragIcon);

                }
                else
                {
                    //Debug.Log($"NOT placing the ship because it's hollow ({hollow}) and the maxWidth is more than or equal to 3 ({maxWidth}) and the shipIndex" +
                    //    $"is Not equal to {sideCheck - 2} or {sideCheck - 1} ({ships})");
                    dragIcons.Add(dragIcon);
                }

            }
            return dropped;
        }
        public void BoxFormation()
        {
            //Debug.Log($"Making a box formation");
            List<DragIcon> dragIcons = GetDragIcons();
            if (dragIcons.Count < 4) // make a line across
            {
                LineMaker(dragIcons.Count, 1, dragIcons);
            }
            else if (dragIcons.Count == 4) // make a 2x2 square
            {
                MultiLine(2, 2);
            }
            else if (dragIcons.Count < 10) // tile across no wider than 3
            {
                MultiLine(3, 3);
            }
            else if (dragIcons.Count < 17) // tile across no wider than 4
            {
                MultiLine(4, 4);
            }
            else // tile across no wider than 5
            {
                MultiLine(5, ConfigData.Configuration.MaxSquadHeight);
            }
        }
        public void RectangleFormation()
        {
            //Debug.Log($"Making a rectangle formation");
            List<DragIcon> dragIcons = GetDragIcons();
            if (dragIcons.Count < 5)
            {
                _scene.SetFormation("Line");
            }
            else if (dragIcons.Count < 11)
            {
                int lineLength = Math.Clamp((dragIcons.Count) / 2, 3, ConfigData.Configuration.MaxSquadWidth);
                List<DragIcon> validDragIcons = dragIcons;
                List<DragIcon> dropped = new List<DragIcon>();

                // top line
                //Debug.Log($"Making a line of length {lineLength} on row 1 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 1, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count))).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();

                // hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 2, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count)), true).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();

                // bottom line
                //Debug.Log($"Making a line of length {lineLength} on row 3 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 3, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count))).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();
            }
            else
            {
                int lineLength = Math.Clamp((dragIcons.Count - 4) / 2, 3, ConfigData.Configuration.MaxSquadWidth);
                List<DragIcon> validDragIcons = dragIcons;
                List<DragIcon> dropped = new List<DragIcon>();

                // top line
                //Debug.Log($"Making a line of length {lineLength} on row 1 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 1, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count))).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();

                // hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 2, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count)), true).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();

                // second hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 3, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count)), true).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();

                // bottom line
                //Debug.Log($"Making a line of length {lineLength} on row 3 with {validDragIcons.Count} icons left ------------------------------------");
                while (validDragIcons.Count > lineLength)
                {
                    lineLength++;
                }
                LineMaker(lineLength, 4, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count))).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();


            }
        }
        public void PyramidFormation(bool hollow)
        {
            //Debug.Log($"Making a pyramid formation");
            List<DragIcon> dragIcons = GetDragIcons();
            List<DragIcon> validDragIcons = dragIcons;
            List<DragIcon> dropped = new List<DragIcon>();
            for (int row = 0; row < ConfigData.Configuration.MaxSquadHeight && validDragIcons.Count > 0; row++)
            {

                int lineLength = (row * 2) + 1;
                //Debug.Log($"Making a hollow line of length {lineLength} on row {row+1} with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, row + 1, validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count)), hollow).ForEach((di) => { dropped.Add(di); });
                validDragIcons = validDragIcons.Where((i) => !dropped.Contains(i)).ToList();
            }
            if (validDragIcons.Count > 0)
            {
                validDragIcons.ForEach((icon) =>
                {
                    icon.RemoveDragIcon();
                });
                Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("warning"));

            }

        }


        // Utility methods
        public Vector2 MouseDragPosition()
        {
            return Input.mousePosition;
        }
        public void RemoveDragIcons()
        {
            while (_dragIcons.Count > 0)
            {
                _dragIcons.First().RemoveDragIcon();
            }
            _currentDragIcon = null;
        }
        public GameObject MakeDeadShipBox(FleetShip fleetShip)
        {
            string name = $"Dead Ship Box #{fleetShip.Id}";

            GameObject deadShipBox = GameObject.Instantiate(_scene.DeadShipBox);
            deadShipBox.transform.SetParent(_scene.DeadShipBox.transform.parent, false);
            deadShipBox.name = name;
            //_deadShipBoxes.Add(deadShipBox);
            deadShipBox.SetActive(true);
            return deadShipBox;
        }
        public void RemoveDragIcon(DragIcon dragIcon)
        {
            FleetShip fleetShip = dragIcon.GetFleetShip();

            SavedSquad dragIconsSquad = ConfigData.CurrentShips.GetSavedSquadFromFleetShip(fleetShip);
            bool isShipInSquad = false;
            bool isShipsSquadThisSquad = false;
            if (dragIconsSquad != null && HasCurrentSquad)
            {
                isShipInSquad = ConfigData.CurrentShips.IsShipInSquad(fleetShip);
                isShipsSquadThisSquad = dragIconsSquad.Equals(CurrentSquad);
                //Debug.Log($"Trying to remove ship from unsaved squad. The squad is {squad.Name} #{squad.Id}, and the " +
                //    $"currentUnsavedSquad is {_currentUnsavedSquad.Id}, and are they equal? {isSquadThisSquad}");
            }

            _dragIcons.Remove(dragIcon);

            // if the drag icon fleetship is not in a saved squad of it is in a saved squad but it's the current squad, add the ship back to the fleet list
            if ((!isShipInSquad || isShipsSquadThisSquad) && !fleetShip.IsDead)
            {
                FleetList.Add(fleetShip);
            }

            // update fleet list count
            _scene.UpdateShipCounter(fleetShip);

            // destroy the dead ship box if it exists
            if (dragIcon.GetDeadShipBox() != null)
            {
                GameObject.Destroy(dragIcon.GetDeadShipBox());
            }

            // destroy the game icon
            GameObject.Destroy(dragIcon.GetIcon());

            // change the color of the squad ship counter
            Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("squad-ship-counter"));

            //if that was the last ship in the current squad, clear the squad
            if (HasCurrentSquad && CurrentSquad.IsEmptySquad)
            {
                _scene.ClearUnsavedSquad();
            }
        }
        public void ResetDrag()
        {
            _currentDragIcon = null;
            _isDragging = false;
            _scene.DropBox.SetActive(false);
            _scene.DragStatusBox.SetActive(false);
            _scene.UpdateSquadUI();
        }


        // Check dragging location validity
        private bool CheckValidDropLocation(Vector2 position, bool shouldSnapPosition, SquadShip ship, ConfigData.ShipTypes shipType)
        {

            if (HasHitDropBox(position))             // check if it's in the squad composition box
            {
                //Debug.Log("Has hit drop box");
                if (HasCurrentSquad && CurrentSquad.HasShips)
                {
                    //Vector2 screenPoint = _scene.Camera.WorldToScreenPoint(ConfigData.ShipOffset);
                    //Vector2 tooClose = new Vector2(Mathf.Abs(_scene.BaseWorldPoint.x - screenPoint.x)-.1f, Mathf.Abs(_scene.BaseWorldPoint.y - screenPoint.y)-.1f);

                    //Vector2 screenPixels = Utilities.WorldUnitsToScreenPixels(ConfigData.GetShipOffsetInWorldUnits(_scene.Camera), _scene.Camera) * _scene.ScreenScaleFactor;
                    Vector2 tooClose = Utilities.WorldUnitsToScreenPixels(ConfigData.ShipOffset, _scene.Camera);

                    //Debug.Log($"Ship offset world units: {ConfigData.ShipOffset}, screen pixels {screenPixels}, tooClose {tooClose}");
                    //Debug.Log($"Offset Vector: {ConfigData.ShipOffset}, Offset change: {tooClose}, Offset VectorToScreen: {screenPoint}, Base World Point:{_scene.BaseWorldPoint}");
                    if (NotTooCloseToSquadShips(position, tooClose, ship))// check if it's too close to other ships
                    {             

                        if (shouldSnapPosition)             // snap to other ships if necessary
                        {
                            position = SnapPosition(position, tooClose);
                            _currentDragIcon.SetPosition(position);
                            _scene.DragStatusBox.transform.position = position;
                        }

                        return true; // got to the squad box and in good position compared to other ships
                    }
                    else
                    {
                        //Debug.Log($"Too close to other ships");
                        return false; // got to the squad box but too close to other ships
                    }
                }
                else
                {
                    return true; // got to the squad composition box and this is the first ship
                }
            }
            else
            {
                //Debug.Log("Didn't hit the squad drop box -----------------------------");
                return false; // didn't drag over the squad maker box
            }
        }
        private bool HasHitDropBox(Vector2 position)
        {

            PointerEventData eventData = new PointerEventData(EventSystem.current);
            position = new Vector2(position.x, position.y);
            eventData.position = position;
            _scene.DropZone.transform.position = (Vector2)_scene.DropZone.transform.position;
            //Debug.Log($"Raycasting from {position}, trying to hit {_scene.DropZone.name} at {_scene.DropZone.transform.position}, autoplacing: {_isAutoPlacing}");


            List<RaycastResult> results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, results);
            RaycastResult thirdHit = results.GetRange(2, 1).FirstOrDefault();
            if (results.Count >= 3)
            {
                thirdHit = results.GetRange(2, 1).FirstOrDefault();
                //RaycastResult firstHit = results.GetRange(0, 1).FirstOrDefault();
                RaycastResult fourthHit = results.GetRange(0, 1).FirstOrDefault();
                bool hasFourHits = false;
                if (results.Count >= 4)
                {
                    hasFourHits = true;
                    fourthHit = results.GetRange(3, 1).FirstOrDefault();
                }


                foreach (RaycastResult hit in results)
                {
                    //Debug.Log($"This raycast hit {hit.gameObject.name}, Hit #3 is {thirdHit.gameObject.name}"); 
                    if (hit.gameObject == _scene.DropZone && 
                        (
                            (
                                hit.Equals(thirdHit) || 
                                (hasFourHits && hit.Equals(fourthHit) && _currentDragIcon.HasDeadShipBox)
                            ) || 
                            _isAutoPlacing
                        )
                    )
                    {
                        return true;
                    }
                }
            }

            //Debug.Log($"Third hit {thirdHit} ---------------------------------------");
            return false;
        }
        private bool NotTooCloseToSquadShips(Vector2 position, Vector2 tooClose, SquadShip ship)
        {
            bool isInValidPositionWithOtherShips = true;

            // Loop through all the other positions in this unsaved squad
            CurrentSquad.GetSquadShips().ForEach((squadShip) =>
            {
                // if any are within too close x and y move the current one away
                if ((ship == null || !ship.Equals(squadShip)) && TooCloseToShip(position, squadShip, tooClose))
                {
                    //Debug.Log($"current ship {_dragIcon.GetFleetShip().Type} is too close to {ship.ShipType} and needs to move");
                    //Debug.Log($"X: {TooCloseToX(position, ship, tooCloseX)}, Y: {TooCloseToY(position, ship, tooCloseY)}");
                    //Debug.Log($"LS: {TooCloseToLeftSide(position, ship, tooCloseX)}, RS: {TooCloseToRightSide(position, ship, tooCloseX)}," +
                    //    $" TS: {TooCloseToTopSide(position, ship, tooCloseY)}, BS: {TooCloseToBottomSide(position, ship, tooCloseY)}");
                    //Debug.Log($"This ship is too close to {squadShip.GetFleetShip().Name} X: {Mathf.Abs(squadShip.GetOffsetInScreenPixels(_scene.Camera).x - position.x)}, " +
                    //    $"Y: {Mathf.Abs(squadShip.GetOffsetInScreenPixels(_scene.Camera).y - position.y)}, " +
                    //    $"Tooclose: {tooClose}");
                    isInValidPositionWithOtherShips = false;
                    return; // break out of the foreach because if it's too close to *any* ship it's invalid
                }
                else
                {
                    //Debug.Log($"Ship position: {ship.GetOffsetInScreenPixels(_scene.Camera)}");
                    //Debug.Log($"Drag position: {position}");
                    //Debug.Log($"NOT too close to ship X: {Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).x - position.x)}, Y: {Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).y - position.y)}");

                }

            });
            return isInValidPositionWithOtherShips;
        }
        private bool TooCloseToShip(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            return TooCloseToX(position, ship, tooClose.x) && TooCloseToY(position, ship, tooClose.y);
        }
        private bool TooCloseToX(Vector2 position, SquadShip ship, float tooClose)
        {
            float placedShipTooClose = Utilities.WorldUnitsToScreenPixels(ConfigData.ShipOffset, _scene.Camera).x;
            float absolutePosition = Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).x - position.x);
            return absolutePosition < tooClose || absolutePosition < placedShipTooClose;
        }
        private bool TooCloseToY(Vector2 position, SquadShip ship, float tooClose)
        {
            float placedShipTooClose = Utilities.WorldUnitsToScreenPixels(ConfigData.ShipOffset, _scene.Camera).y;
            float absolutePosition = Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).y - position.y);
            return absolutePosition < tooClose || absolutePosition < placedShipTooClose;
        }


        // Check ship snapping
        private Vector2 SnapPosition(Vector2 position, Vector2 tooClose)
        {
            //Vector2 screenPoint = _scene.Camera.WorldToScreenPoint(ConfigData.SnapDistance);
            //Vector2 snap = new Vector2(Mathf.Abs(_scene.BaseWorldPoint.x - screenPoint.x), Mathf.Abs(_scene.BaseWorldPoint.y - screenPoint.y));

            Vector2 snap = Utilities.WorldUnitsToScreenPixels(ConfigData.SnapDistance, _scene.Camera);

            // Loop through all the other positions in this unsaved squad
            //Debug.Log("Snap -------------------------------------------------------");
            CurrentSquad.GetSquadShips().ForEach((ship) =>
            {
                if (ShouldSnapToXAxis(position, ship, snap.x))   // if any are within too close x or y, but not both, snap the current one to line up
                {
                    //Debug.Log($"Snapping the drag ship to the X axis of {ship.ShipType}, currentPosition: {position}");
                    position = SnapX(position, ship, tooClose);
                }
                else if (ShouldSnapToYAxis(position, ship, snap.y))
                {
                    //Debug.Log($"Snapping the drag ship to the Y axis of {ship.ShipType}, currentPosition: {position}");
                    position = SnapY(position, ship, tooClose);
                }

            });
            return position;
        }
        private Vector2 SnapY(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            Vector2 newPosition = new Vector2(position.x, ship.GetOffsetInScreenPixels(_scene.Camera).y);

            if (TooCloseToShip(newPosition, ship, tooClose))
            {
                return position;
            }
            else
            {
                // try to snap symmetric X
                position = newPosition;
                //Debug.Log($"Snapped the drag ship to the Y axis of {ship.ShipType}, now trying to snap the SYMMETRIC X, currentPosition: {position}");
                newPosition = SnapSymmetricAxis(newPosition, ship, 'x');
                //Debug.Log($"Snapped the drag ship to the SYMMETRIC X axis of {ship.ShipType}, currentPosition: {newPosition}");
                if (!CheckValidDropLocation(newPosition, false, null, ship.ShipType) || Mathf.Abs(newPosition.x - position.x) >= tooClose.x)
                {
                    return position;
                }
                else
                {
                    return newPosition;
                }
            }

        }
        public Vector2 GetOddSymmetricPoint(List<SquadShip> sameLevelShips, Vector2 position, char axis)
        {
            SquadShip least;
            SquadShip most;
            if (axis == 'x')
            {
                // get the (nth+1)/2 ship e.g 9 ships : 9+1 = 10, 10/2 = 5, get the 5th ship (index 4)
                sameLevelShips = sameLevelShips.OrderBy((s) => s.Offset.x).ToList();

                // get least axis ship
                least = sameLevelShips.OrderBy((s) => s.Offset.x).First();

                // get most axis ship
                most = sameLevelShips.OrderByDescending((s) => s.Offset.x).First();


            }
            else
            {
                sameLevelShips = sameLevelShips.OrderBy((s) => s.Offset.y).ToList();
                least = sameLevelShips.OrderBy((s) => s.Offset.y).First();
                most = sameLevelShips.OrderByDescending((s) => s.Offset.y).First();

            }
            int index = ((sameLevelShips.Count + 1) / 2) - 1;
            SquadShip middle = sameLevelShips.GetRange(index, 1).First();


            if (axis == 'x')
            {
                // if your dragging ship is between them then you want the center point between the two sides 
                if (position.x > least.GetOffsetInScreenPixels(_scene.Camera).x && position.x < most.GetOffsetInScreenPixels(_scene.Camera).x)
                {
                    // get the point between them
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X in the middle between ships");
                    float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).x - most.GetOffsetInScreenPixels(_scene.Camera).x);
                    return new Vector2(least.GetOffsetInScreenPixels(_scene.Camera).x + distance / 2, position.y);
                }
                else if (position.x > most.GetOffsetInScreenPixels(_scene.Camera).x) // if your dragging ship is more than them, then you want the distance of the axis most ship from the center
                {
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X on the right side of of right most ship {most.ShipType}");
                    float distance = Mathf.Abs(middle.GetOffsetInScreenPixels(_scene.Camera).x - most.GetOffsetInScreenPixels(_scene.Camera).x);
                    return new Vector2(most.GetOffsetInScreenPixels(_scene.Camera).x + distance, position.y);
                }
                else // if your dragging ship is less than them
                {
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X on the left side of of left most ship {most.ShipType}");
                    float distance = Mathf.Abs(middle.GetOffsetInScreenPixels(_scene.Camera).x - least.GetOffsetInScreenPixels(_scene.Camera).x);
                    return new Vector2(least.GetOffsetInScreenPixels(_scene.Camera).x - distance, position.y);
                }
            }
            else
            {
                if (position.y > least.GetOffsetInScreenPixels(_scene.Camera).y && position.y < most.GetOffsetInScreenPixels(_scene.Camera).y)
                {
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y in the middle between ships");
                    float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).y - most.GetOffsetInScreenPixels(_scene.Camera).y);
                    return new Vector2(position.x, least.GetOffsetInScreenPixels(_scene.Camera).y + distance / 2);
                }
                else if (position.y > most.GetOffsetInScreenPixels(_scene.Camera).y)
                {
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y on the top side of of top most ship {most.ShipType}");
                    float distance = Mathf.Abs(middle.GetOffsetInScreenPixels(_scene.Camera).y - most.GetOffsetInScreenPixels(_scene.Camera).y);
                    return new Vector2(position.x, most.GetOffsetInScreenPixels(_scene.Camera).y + distance);
                }
                else
                {
                    //Debug.Log($"There are {sameLevelShips.Count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y on the bottom side of of bottom most ship {most.ShipType}");
                    float distance = Mathf.Abs(middle.GetOffsetInScreenPixels(_scene.Camera).y - least.GetOffsetInScreenPixels(_scene.Camera).y);
                    return new Vector2(position.x, least.GetOffsetInScreenPixels(_scene.Camera).y - distance);
                }
            }


        }
        private Vector2 GetEvenSymmetricPoint(List<SquadShip> sameLevelShips, Vector2 position, char axis)
        {
            SquadShip possibleCenter = null;
            bool placeBetweenShips = false;
            int count = sameLevelShips.Count;
            SquadShip least;
            SquadShip most;
            if (axis == 'x')
            {
                // get least axis ship
                least = sameLevelShips.OrderBy((s) => s.Offset.x).First();

                // get most axis ship
                most = sameLevelShips.OrderByDescending((s) => s.Offset.x).First();

                sameLevelShips.ForEach((squadShip) =>
                {
                    int lessThan = 0;
                    int moreThan = 0;

                    sameLevelShips.ForEach((comparisonShip) =>
                    {
                        if (comparisonShip.GetOffsetInScreenPixels(_scene.Camera).x != squadShip.GetOffsetInScreenPixels(_scene.Camera).x) // [alert] use Equals here instead
                        {
                            if (comparisonShip.GetOffsetInScreenPixels(_scene.Camera).x < squadShip.GetOffsetInScreenPixels(_scene.Camera).x)
                            {
                                lessThan++;
                            }
                            else
                            {
                                moreThan++;
                            }
                        }

                    });

                    if ((lessThan == count / 2 && moreThan == (count / 2) - 1) || (moreThan == count / 2 && lessThan == (count / 2) - 1))
                    // if half are on one side and half minus one are on the other side then this is a potential center ship
                    {
                        if (lessThan > moreThan && position.x > squadShip.GetOffsetInScreenPixels(_scene.Camera).x) // if there are more ships on the left and dragged ship is on the right, place on the right
                        {
                            possibleCenter = squadShip;
                            return;
                        }
                        else if (moreThan > lessThan && position.x < squadShip.GetOffsetInScreenPixels(_scene.Camera).x)  // if there are more ships on the right and dragged ship is on the left, place on the left
                        {
                            possibleCenter = squadShip;
                            return;
                        }
                        else if (moreThan > lessThan && position.x > squadShip.GetOffsetInScreenPixels(_scene.Camera).x)
                        {
                            placeBetweenShips = true;
                            return;
                        }
                    }
                });
                if (possibleCenter != null)
                {
                    if (position.x > possibleCenter.GetOffsetInScreenPixels(_scene.Camera).x) // if the dragging ship is right of the center
                    {
                        //Debug.Log($"There are {count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X on the right side of center ship {possibleCenter.ShipType}");
                        float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).x - possibleCenter.GetOffsetInScreenPixels(_scene.Camera).x);
                        return new Vector2(possibleCenter.GetOffsetInScreenPixels(_scene.Camera).x + distance, position.y);
                    }
                    else
                    {
                        //Debug.Log($"There are {count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X on the left side of center ship {possibleCenter.ShipType}");
                        float distance = Mathf.Abs(most.GetOffsetInScreenPixels(_scene.Camera).x - possibleCenter.GetOffsetInScreenPixels(_scene.Camera).x);
                        return new Vector2(possibleCenter.GetOffsetInScreenPixels(_scene.Camera).x - distance, position.y);
                    }
                }
                else
                {
                    if (placeBetweenShips)
                    {
                        //Debug.Log($"There are {count} ships that share the same Y axis with the drag ship. We are snapping the symmetric X in the middle between ships");
                        float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).x - most.GetOffsetInScreenPixels(_scene.Camera).x);
                        return new Vector2(least.GetOffsetInScreenPixels(_scene.Camera).x + distance / 2, position.y);
                    }
                    else
                    {
                        //Debug.Log($"There are {count} ships that share the same Y axis with the drag ship. We did not find a symmetric point to snap to.");
                        return position;
                    }
                }
            }
            else
            {
                least = sameLevelShips.OrderBy((s) => s.GetOffsetInScreenPixels(_scene.Camera).y).First();
                most = sameLevelShips.OrderByDescending((s) => s.GetOffsetInScreenPixels(_scene.Camera).y).First();

                sameLevelShips.ForEach((squadShip) =>
                {
                    int lessThan = 0;
                    int moreThan = 0;

                    sameLevelShips.ForEach((comparisonShip) =>
                    {
                        if (comparisonShip.GetOffsetInScreenPixels(_scene.Camera).y != squadShip.GetOffsetInScreenPixels(_scene.Camera).y) // [alert] use Equals here instead
                        {
                            if (comparisonShip.GetOffsetInScreenPixels(_scene.Camera).y < squadShip.GetOffsetInScreenPixels(_scene.Camera).y)
                            {
                                lessThan++;
                            }
                            else
                            {
                                moreThan++;
                            }
                        }

                    });

                    if ((lessThan == count / 2 && moreThan == (count / 2) - 1) || (moreThan == count / 2 && lessThan == (count / 2) - 1))
                    {
                        if (lessThan > moreThan && position.y > squadShip.GetOffsetInScreenPixels(_scene.Camera).y)
                        {
                            possibleCenter = squadShip;
                            return;
                        }
                        else if (moreThan > lessThan && position.y < squadShip.GetOffsetInScreenPixels(_scene.Camera).y)
                        {
                            possibleCenter = squadShip;
                            return;
                        }
                        else if (moreThan > lessThan && position.y > squadShip.GetOffsetInScreenPixels(_scene.Camera).y)
                        {   
                            placeBetweenShips = true;
                            return;
                        }
                    }
                });
                if (possibleCenter != null)
                {
                    if (position.y > possibleCenter.GetOffsetInScreenPixels(_scene.Camera).y)
                    {
                        //Debug.Log($"There are {count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y on the top side of center ship {possibleCenter.ShipType}");
                        float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).y - possibleCenter.GetOffsetInScreenPixels(_scene.Camera).y);
                        return new Vector2(position.x, possibleCenter.GetOffsetInScreenPixels(_scene.Camera).y + distance);
                    }
                    else
                    {
                        //Debug.Log($"There are {count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y on the bottom side of center ship {possibleCenter.ShipType}");
                        float distance = Mathf.Abs(most.GetOffsetInScreenPixels(_scene.Camera).y - possibleCenter.GetOffsetInScreenPixels(_scene.Camera).y);
                        return new Vector2(position.x, possibleCenter.GetOffsetInScreenPixels(_scene.Camera).y - distance);
                    }
                }
                else
                {
                    if (placeBetweenShips)
                    {
                        //Debug.Log($"There are {count} ships that share the same X axis with the drag ship. We are snapping the symmetric Y in the middle between ships");
                        float distance = Mathf.Abs(least.GetOffsetInScreenPixels(_scene.Camera).y - most.GetOffsetInScreenPixels(_scene.Camera).y);
                        return new Vector2(position.x, least.GetOffsetInScreenPixels(_scene.Camera).y + distance / 2);
                    }
                    else
                    {
                        //Debug.Log($"There are {count} ships that share the same X axis with the drag ship. We did not find a symmetric point to snap to.");
                        return position;
                    }
                }
            }

        }
        private Vector2 SnapSymmetricAxis(Vector2 position, SquadShip ship, char axis)
        {
            // when fleet ships are properly tracked and implemented, this should call the Equals() method
            List<SquadShip> sameLevelShips;
            if (axis == 'x')
            {
                sameLevelShips = CurrentSquad.GetSquadShips().Where((squadShip) => squadShip.Offset.y == ship.Offset.y).ToList();
            }
            else
            {
                sameLevelShips = CurrentSquad.GetSquadShips().Where((squadShip) => squadShip.Offset.x == ship.Offset.x).ToList();
            }

            int count = sameLevelShips.Count;
            if (count > 1) // should be zero
            {
                if (sameLevelShips.Count % 2 == 0) // even number of ships
                {
                    return GetEvenSymmetricPoint(sameLevelShips, position, axis);
                }
                else // odd number of ships
                {
                    return GetOddSymmetricPoint(sameLevelShips, position, axis);
                }
            }
            else
            {
                //Debug.Log($"There was only one other ship on the same {axis} axis,looking for a symmetric snap based on ship(s) above.");
                return position;
            }

        }
        private Vector2 SnapX(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            Vector2 newPosition = new Vector2(ship.GetOffsetInScreenPixels(_scene.Camera).x, position.y);
            if (TooCloseToShip(newPosition, ship, tooClose))
            {
                return position;
            }
            else
            {
                // try to snap symmetric Y
                position = newPosition;
                //Debug.Log($"Snapped the drag ship to the X axis of {ship.ShipType}, now trying to snap the SYMMETRIC Y, currentPosition: {position}");
                newPosition = SnapSymmetricAxis(newPosition, ship, 'y');
                //Debug.Log($"Snapped the drag ship to the SYMMETRIC Y axis of {ship.ShipType}, currentPosition: {newPosition}");
                if (!CheckValidDropLocation(newPosition, false, null, ship.ShipType) || Mathf.Abs(newPosition.y - position.y) >= tooClose.y)
                {
                    return position;
                }
                else
                {
                    return newPosition;
                }
            }

        }
        private bool ShouldSnapToYAxis(Vector2 position, SquadShip ship, float tooClose)
        {
            return Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).y - position.y) <= tooClose;
        }
        private bool ShouldSnapToXAxis(Vector2 position, SquadShip ship, float tooClose)
        {
            return Mathf.Abs(ship.GetOffsetInScreenPixels(_scene.Camera).x - position.x) <= tooClose;
        }

    }
}