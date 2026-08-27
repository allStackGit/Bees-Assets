using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Owns Squad Maker ship placement. Gameplay decisions are made in canonical world offsets;
    /// screen coordinates are presentation/input only and are converted through one fixed logical
    /// drag workspace.
    /// </summary>
    public class Dropper
    {
        private readonly SquadMaker _scene;
        private readonly SquadMakerDragWorkspace _workspace;
        private bool _isAutoPlacing;
        private bool _isDragging;
        private readonly List<DragIcon> _dragIcons = new List<DragIcon>();
        private DragIcon _currentDragIcon;
        private int _dragIconCount;

        public bool IsValidDropLocation;
        public bool IsDragging => _isDragging;
        public SavedSquad CurrentSquad => _scene.GetCurrentSquad();
        public bool HasCurrentSquad => CurrentSquad != null;

        public Dropper(SquadMaker scene)
        {
            _scene = scene;
            _workspace = new SquadMakerDragWorkspace(scene);
        }

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

        public void PullNewDragIcon(ConfigData.ShipTypes shipType)
        {
            FleetShip fleetShip = ConfigData.CurrentShips.GetFirstAvailableShipOfType(shipType);
            Debug.Log($"Pulled {fleetShip} for drag icon");

            if (fleetShip != null && !_isDragging && (!HasCurrentSquad || !CurrentSquad.HasMaxShips))
            {
                MakeDragIcon(fleetShip);
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
            string name = $"{fleetShip.Name} #{fleetShip.Id}";
            GameObject dragIcon = GameObject.Instantiate(dragIconPrefab);
            UnityEngine.UI.Image image = dragIcon.GetComponent<UnityEngine.UI.Image>();
            dragIcon.transform.SetParent(dragIconPrefab.transform.parent, false);
            image.SetNativeSize();
            dragIcon.transform.localScale = BaseIconScale(fleetShip.Type);

            DragIcon newDragIcon = new DragIcon(_scene, dragIcon, fleetShip, name, _dragIconCount++);
            _dragIcons.Add(newDragIcon);
            _currentDragIcon = newDragIcon;
        }

        public void SetupActiveDragging(Vector2 position, bool isAutoPlacing)
        {
            if (_currentDragIcon == null)
            {
                return;
            }

            _workspace.RefreshVisualFit();
            _isAutoPlacing = isAutoPlacing;
            _isDragging = true;
            IsValidDropLocation = false;

            Vector2 screenPosition = position;
            if (_currentDragIcon.HasWorkspaceOffset)
            {
                screenPosition = _workspace.WorldOffsetToScreen(_currentDragIcon.WorkspaceOffset);
            }
            else if (isAutoPlacing)
            {
                Vector2 origin = _workspace.FormationOriginWorldOffset;
                _currentDragIcon.SetWorkspaceOffset(origin);
                screenPosition = _workspace.WorldOffsetToScreen(origin);
            }

            SetIconScreenPosition(_currentDragIcon, screenPosition);
            ConfigureDragStatus(screenPosition);
            _scene.DragStatusBox.SetActive(true);
            _scene.DropBox.SetActive(true);
            _scene.UpdateShipCounter(_currentDragIcon.GetFleetShip().Type);
            _scene.DelayedHideShipStats();
        }

        /// <summary>
        /// Legacy compatibility entry point. Screen positions are immediately converted into the
        /// fixed logical workspace and never persisted directly.
        /// </summary>
        public bool PlaceShipAtPosition(Vector2 position, SquadShip ship)
        {
            _isAutoPlacing = true;
            if (_currentDragIcon == null)
            {
                IsValidDropLocation = false;
                return false;
            }

            if (ship != null)
            {
                return PlaceShipAtWorldOffset(ship.Offset, ship);
            }

            if (_currentDragIcon.HasWorkspaceOffset)
            {
                return PlaceShipAtWorldOffset(_currentDragIcon.WorkspaceOffset, null);
            }

            if (!_workspace.TryScreenToWorldOffset(position, out Vector2 worldOffset))
            {
                SetIconScreenPosition(_currentDragIcon, position);
                _scene.DragStatusBox.transform.position = position;
                IsValidDropLocation = false;
                return false;
            }

            return PlaceShipAtWorldOffset(worldOffset, null);
        }

        public bool PlaceShipAtWorldOffset(Vector2 worldOffset, SquadShip ship)
        {
            _isAutoPlacing = true;
            if (_currentDragIcon == null)
            {
                IsValidDropLocation = false;
                return false;
            }

            IsValidDropLocation = CheckValidDropLocation(
                worldOffset,
                false,
                ship,
                _currentDragIcon.GetFleetShip().Type,
                out Vector2 acceptedOffset);

            if (IsValidDropLocation)
            {
                _currentDragIcon.SetWorkspaceOffset(acceptedOffset);
                Vector2 screenPosition = _workspace.WorldOffsetToScreen(acceptedOffset);
                SetIconScreenPosition(_currentDragIcon, screenPosition);
                _scene.DragStatusBox.transform.position = screenPosition;
                PositionDeadShipBox(_currentDragIcon, screenPosition);
            }
            else
            {
                Vector2 screenPosition = _workspace.WorldOffsetToScreen(worldOffset);
                SetIconScreenPosition(_currentDragIcon, screenPosition);
                _scene.DragStatusBox.transform.position = screenPosition;
            }

            return IsValidDropLocation;
        }

        public void AutoPlaceShip(ConfigData.ShipTypes shipType)
        {
            if (_isDragging)
            {
                return;
            }

            PullNewDragIcon(shipType);
            if (_currentDragIcon == null)
            {
                return;
            }

            Vector2 origin = _workspace.FormationOriginWorldOffset;
            SetupActiveDragging(_workspace.WorldOffsetToScreen(origin), true);
            PlaceShipAtWorldOffset(origin, null);
            _scene.FleetDragEnd();
            _scene.SetFormation(_scene.CurrentFormation);
        }

        public void StartDragExistingIcon(FleetShip fleetShip)
        {
            _currentDragIcon = GetDragIcons().Find(d => d.GetFleetShip() == fleetShip);
        }

        public void DraggingNewIcon()
        {
            if (_currentDragIcon == null)
            {
                return;
            }

            _isDragging = true;
            _isAutoPlacing = false;
            Vector2 pointer = MouseDragPosition();
            SetIconScreenPosition(_currentDragIcon, pointer);
            _scene.DragStatusBox.transform.position = pointer;
            PositionDeadShipBox(_currentDragIcon, pointer);

            if (!_workspace.TryScreenToWorldOffset(pointer, out Vector2 candidate))
            {
                Utilities.SetBadColor(_scene.DragStatusBox);
                IsValidDropLocation = false;
                return;
            }

            if (CheckValidDropLocation(
                candidate,
                true,
                null,
                _currentDragIcon.GetFleetShip().Type,
                out Vector2 acceptedOffset))
            {
                _currentDragIcon.SetWorkspaceOffset(acceptedOffset);
                Vector2 snappedScreenPosition = _workspace.WorldOffsetToScreen(acceptedOffset);
                SetIconScreenPosition(_currentDragIcon, snappedScreenPosition);
                _scene.DragStatusBox.transform.position = snappedScreenPosition;
                PositionDeadShipBox(_currentDragIcon, snappedScreenPosition);
                Utilities.SetGoodColor(_scene.DragStatusBox);
                IsValidDropLocation = true;
            }
            else
            {
                Utilities.SetBadColor(_scene.DragStatusBox);
                IsValidDropLocation = false;
            }
        }

        public void EndDragging()
        {
            if (_currentDragIcon != null)
            {
                if (IsValidDropLocation && _currentDragIcon.HasWorkspaceOffset)
                {
                    Color squadColor = _scene.GetSquadColor();
                    Vector2 worldPointPosition = _currentDragIcon.WorkspaceOffset;
                    Vector2 screenPosition = _workspace.WorldOffsetToScreen(worldPointPosition);
                    FleetShip fleetShip = _currentDragIcon.GetFleetShip();

                    _currentDragIcon.SetColor(squadColor);
                    Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("squad-ship-counter"));

                    if (fleetShip.IsDead && !_currentDragIcon.HasDeadShipBox)
                    {
                        GameObject deadShipBox = MakeDeadShipBox(fleetShip);
                        deadShipBox.transform.position = screenPosition;
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
                            CurrentSquad.GetShip(fleetShip.Id).SetOffset(worldPointPosition);
                        }
                        CurrentSquad.SetChanged(true);
                    }
                    else
                    {
                        _scene.SetCurentSquad(new SavedSquad(
                            -1,
                            _scene.Side,
                            _scene.GetSquadName(),
                            worldPointPosition,
                            false,
                            false,
                            ConfigData.DefaultShootingStrategy,
                            squadColor));

                        CurrentSquad.SetChanged(true);
                        CurrentSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, worldPointPosition));

                        if (_scene.HasActionBox)
                        {
                            _scene.ActionBox.SetupForSquad();
                        }
                    }

                    _scene.UpdateShipCounter(fleetShip.Type);
                }
                else
                {
                    Debug.Log("INVALID PLACEMENT. REMOVING DRAG ICON");
                    _currentDragIcon.RemoveDragIcon();
                }
            }

            ResetDrag();
        }

        // Formation placement -------------------------------------------------------------

        public void LineFormation()
        {
            MultiLine(ConfigData.Configuration.MaxSquadWidth, ConfigData.Configuration.MaxSquadHeight);
        }

        public void MultiLine(int maxWidth, int maxLines, bool hollow = false)
        {
            List<DragIcon> validDragIcons = GetDragIcons().ToList();
            List<DragIcon> dropped = new List<DragIcon>();
            for (int row = 0; row < maxLines && validDragIcons.Count > 0; row++)
            {
                List<DragIcon> rowIcons = validDragIcons.GetRange(0, Math.Clamp(maxWidth, 0, validDragIcons.Count));
                dropped.AddRange(LineMaker(maxWidth, row + 1, rowIcons, hollow));
                validDragIcons = validDragIcons.Where(i => !dropped.Contains(i)).ToList();
            }
        }

        public List<DragIcon> LineMaker(int maxWidth, int level, List<DragIcon> dragIcons, bool hollow = false)
        {
            if (dragIcons == null)
            {
                dragIcons = GetDragIcons().ToList();
            }

            level = Math.Clamp(level - 1, 0, ConfigData.Configuration.MaxSquadHeight);
            maxWidth = Math.Clamp(maxWidth, 0, ConfigData.Configuration.MaxSquadWidth);
            List<DragIcon> dropped = new List<DragIcon>();
            Vector2 change = ConfigData.ShipOffset * 1.05f;
            Vector2 origin = _workspace.FormationOriginWorldOffset;

            for (int ships = 0; ships < maxWidth && ships < dragIcons.Count; ships++)
            {
                DragIcon dragIcon = dragIcons[ships];
                float movement;
                if (ships % 2 == 0)
                {
                    int sideSteps = (int)Math.Floor((double)ships / 2);
                    movement = sideSteps * change.x;
                }
                else
                {
                    int sideSteps = (int)Math.Ceiling((double)ships / 2);
                    movement = -sideSteps * change.x;
                }

                int sideCheck = maxWidth;
                if (!hollow || maxWidth < 3 || ships == sideCheck - 2 || ships == sideCheck - 1)
                {
                    Vector2 movedOffset = new Vector2(
                        origin.x + movement,
                        origin.y - level * change.y);
                    dragIcon.RepositionWorldOffset(movedOffset, null);
                    dropped.Add(dragIcon);
                }
            }

            return dropped;
        }

        public void BoxFormation()
        {
            List<DragIcon> dragIcons = GetDragIcons();
            if (dragIcons.Count < 4)
            {
                LineMaker(dragIcons.Count, 1, dragIcons.ToList());
            }
            else if (dragIcons.Count == 4)
            {
                MultiLine(2, 2);
            }
            else if (dragIcons.Count < 10)
            {
                MultiLine(3, 3);
            }
            else if (dragIcons.Count < 17)
            {
                MultiLine(4, 4);
            }
            else
            {
                MultiLine(5, ConfigData.Configuration.MaxSquadHeight);
            }
        }

        public void RectangleFormation()
        {
            List<DragIcon> dragIcons = GetDragIcons();
            if (dragIcons.Count < 5)
            {
                _scene.SetFormation("Line");
                return;
            }

            int lineLength = dragIcons.Count < 11
                ? Math.Clamp(dragIcons.Count / 2, 3, ConfigData.Configuration.MaxSquadWidth)
                : Math.Clamp((dragIcons.Count - 4) / 2, 3, ConfigData.Configuration.MaxSquadWidth);

            List<DragIcon> validDragIcons = dragIcons.ToList();
            List<DragIcon> dropped = new List<DragIcon>();
            DropRectangleRow(ref validDragIcons, dropped, lineLength, 1, false);
            DropRectangleRow(ref validDragIcons, dropped, lineLength, 2, true);

            if (dragIcons.Count >= 11)
            {
                DropRectangleRow(ref validDragIcons, dropped, lineLength, 3, true);
                while (validDragIcons.Count > lineLength)
                {
                    lineLength++;
                }
                DropRectangleRow(ref validDragIcons, dropped, lineLength, 4, false);
            }
            else
            {
                DropRectangleRow(ref validDragIcons, dropped, lineLength, 3, false);
            }
        }

        public void PyramidFormation(bool hollow)
        {
            List<DragIcon> validDragIcons = GetDragIcons().ToList();
            List<DragIcon> dropped = new List<DragIcon>();
            for (int row = 0; row < ConfigData.Configuration.MaxSquadHeight && validDragIcons.Count > 0; row++)
            {
                int lineLength = (row * 2) + 1;
                List<DragIcon> rowIcons = validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count));
                dropped.AddRange(LineMaker(lineLength, row + 1, rowIcons, hollow));
                validDragIcons = validDragIcons.Where(i => !dropped.Contains(i)).ToList();
            }

            if (validDragIcons.Count > 0)
            {
                validDragIcons.ToList().ForEach(icon => icon.RemoveDragIcon());
                Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("warning"));
            }
        }

        private void DropRectangleRow(
            ref List<DragIcon> validDragIcons,
            List<DragIcon> dropped,
            int lineLength,
            int level,
            bool hollow)
        {
            if (validDragIcons.Count == 0)
            {
                return;
            }

            List<DragIcon> rowIcons = validDragIcons.GetRange(0, Math.Clamp(lineLength, 0, validDragIcons.Count));
            dropped.AddRange(LineMaker(lineLength, level, rowIcons, hollow));
            validDragIcons = validDragIcons.Where(i => !dropped.Contains(i)).ToList();
        }

        // Drag lifecycle utilities -------------------------------------------------------

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
            deadShipBox.SetActive(true);
            return deadShipBox;
        }

        public void RemoveDragIcon(DragIcon dragIcon)
        {
            FleetShip fleetShip = dragIcon.GetFleetShip();
            _dragIcons.Remove(dragIcon);

            if (dragIcon.GetDeadShipBox() != null)
            {
                GameObject.Destroy(dragIcon.GetDeadShipBox());
            }

            GameObject.Destroy(dragIcon.GetIcon());
            Utilities.SetUIColor(_scene.SquadShipCount, ConfigData.GetUIColor("squad-ship-counter"));

            if (HasCurrentSquad && CurrentSquad.IsEmptySquad)
            {
                _scene.ClearUnsavedSquad();
            }
        }

        public void ResetDrag()
        {
            _currentDragIcon = null;
            _isDragging = false;
            _isAutoPlacing = false;
            _scene.DropBox.SetActive(false);
            _scene.DragStatusBox.SetActive(false);
            _scene.UpdateSquadUI();
        }

        // Canonical placement validation -------------------------------------------------

        private bool CheckValidDropLocation(
            Vector2 worldOffset,
            bool shouldSnapPosition,
            SquadShip ship,
            ConfigData.ShipTypes shipType,
            out Vector2 acceptedOffset)
        {
            acceptedOffset = worldOffset;
            if (!_workspace.ContainsWorldOffset(worldOffset))
            {
                return false;
            }

            if (!HasCurrentSquad || !CurrentSquad.HasShips)
            {
                return true;
            }

            Vector2 tooClose = ConfigData.ShipOffset;
            if (!NotTooCloseToSquadShips(worldOffset, tooClose, ship))
            {
                return false;
            }

            if (shouldSnapPosition)
            {
                acceptedOffset = SnapPosition(worldOffset, tooClose);
                if (!_workspace.ContainsWorldOffset(acceptedOffset))
                {
                    acceptedOffset = worldOffset;
                }
            }

            return true;
        }

        private bool NotTooCloseToSquadShips(Vector2 position, Vector2 tooClose, SquadShip ship)
        {
            foreach (SquadShip squadShip in CurrentSquad.GetSquadShips())
            {
                if ((ship == null || !ship.Equals(squadShip)) && TooCloseToShip(position, squadShip, tooClose))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TooCloseToShip(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            return Mathf.Abs(ship.Offset.x - position.x) < tooClose.x &&
                   Mathf.Abs(ship.Offset.y - position.y) < tooClose.y;
        }

        private Vector2 SnapPosition(Vector2 position, Vector2 tooClose)
        {
            Vector2 snap = ConfigData.SnapDistance;
            foreach (SquadShip ship in CurrentSquad.GetSquadShips())
            {
                if (ShouldSnapToXAxis(position, ship, snap.x))
                {
                    position = SnapX(position, ship, tooClose);
                }
                else if (ShouldSnapToYAxis(position, ship, snap.y))
                {
                    position = SnapY(position, ship, tooClose);
                }
            }
            return position;
        }

        private Vector2 SnapY(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            Vector2 newPosition = new Vector2(position.x, ship.Offset.y);
            if (TooCloseToShip(newPosition, ship, tooClose))
            {
                return position;
            }

            position = newPosition;
            newPosition = SnapSymmetricAxis(newPosition, ship, 'x');
            if (!CheckValidDropLocation(newPosition, false, null, ship.ShipType, out _) ||
                Mathf.Abs(newPosition.x - position.x) >= tooClose.x)
            {
                return position;
            }
            return newPosition;
        }

        private Vector2 SnapX(Vector2 position, SquadShip ship, Vector2 tooClose)
        {
            Vector2 newPosition = new Vector2(ship.Offset.x, position.y);
            if (TooCloseToShip(newPosition, ship, tooClose))
            {
                return position;
            }

            position = newPosition;
            newPosition = SnapSymmetricAxis(newPosition, ship, 'y');
            if (!CheckValidDropLocation(newPosition, false, null, ship.ShipType, out _) ||
                Mathf.Abs(newPosition.y - position.y) >= tooClose.y)
            {
                return position;
            }
            return newPosition;
        }

        public Vector2 GetOddSymmetricPoint(List<SquadShip> sameLevelShips, Vector2 position, char axis)
        {
            if (sameLevelShips == null || sameLevelShips.Count == 0)
            {
                return position;
            }

            if (axis == 'x')
            {
                List<SquadShip> ordered = sameLevelShips.OrderBy(s => s.Offset.x).ToList();
                SquadShip least = ordered.First();
                SquadShip most = ordered.Last();
                SquadShip middle = ordered[((ordered.Count + 1) / 2) - 1];
                if (position.x > least.Offset.x && position.x < most.Offset.x)
                {
                    return new Vector2((least.Offset.x + most.Offset.x) / 2f, position.y);
                }
                if (position.x > most.Offset.x)
                {
                    return new Vector2(most.Offset.x + Mathf.Abs(middle.Offset.x - most.Offset.x), position.y);
                }
                return new Vector2(least.Offset.x - Mathf.Abs(middle.Offset.x - least.Offset.x), position.y);
            }

            List<SquadShip> vertical = sameLevelShips.OrderBy(s => s.Offset.y).ToList();
            SquadShip bottom = vertical.First();
            SquadShip top = vertical.Last();
            SquadShip verticalMiddle = vertical[((vertical.Count + 1) / 2) - 1];
            if (position.y > bottom.Offset.y && position.y < top.Offset.y)
            {
                return new Vector2(position.x, (bottom.Offset.y + top.Offset.y) / 2f);
            }
            if (position.y > top.Offset.y)
            {
                return new Vector2(position.x, top.Offset.y + Mathf.Abs(verticalMiddle.Offset.y - top.Offset.y));
            }
            return new Vector2(position.x, bottom.Offset.y - Mathf.Abs(verticalMiddle.Offset.y - bottom.Offset.y));
        }

        private Vector2 GetEvenSymmetricPoint(List<SquadShip> sameLevelShips, Vector2 position, char axis)
        {
            if (sameLevelShips == null || sameLevelShips.Count == 0)
            {
                return position;
            }

            SquadShip possibleCenter = null;
            bool placeBetweenShips = false;
            int count = sameLevelShips.Count;

            if (axis == 'x')
            {
                SquadShip least = sameLevelShips.OrderBy(s => s.Offset.x).First();
                SquadShip most = sameLevelShips.OrderByDescending(s => s.Offset.x).First();
                foreach (SquadShip squadShip in sameLevelShips)
                {
                    int lessThan = sameLevelShips.Count(other => other.Offset.x < squadShip.Offset.x);
                    int moreThan = sameLevelShips.Count(other => other.Offset.x > squadShip.Offset.x);
                    if ((lessThan == count / 2 && moreThan == count / 2 - 1) ||
                        (moreThan == count / 2 && lessThan == count / 2 - 1))
                    {
                        if (lessThan > moreThan && position.x > squadShip.Offset.x)
                        {
                            possibleCenter = squadShip;
                            break;
                        }
                        if (moreThan > lessThan && position.x < squadShip.Offset.x)
                        {
                            possibleCenter = squadShip;
                            break;
                        }
                        if (moreThan > lessThan && position.x > squadShip.Offset.x)
                        {
                            placeBetweenShips = true;
                        }
                    }
                }

                if (possibleCenter != null)
                {
                    float distance = position.x > possibleCenter.Offset.x
                        ? Mathf.Abs(least.Offset.x - possibleCenter.Offset.x)
                        : Mathf.Abs(most.Offset.x - possibleCenter.Offset.x);
                    return new Vector2(
                        possibleCenter.Offset.x + (position.x > possibleCenter.Offset.x ? distance : -distance),
                        position.y);
                }

                if (placeBetweenShips)
                {
                    return new Vector2((least.Offset.x + most.Offset.x) / 2f, position.y);
                }
                return position;
            }

            SquadShip bottom = sameLevelShips.OrderBy(s => s.Offset.y).First();
            SquadShip top = sameLevelShips.OrderByDescending(s => s.Offset.y).First();
            foreach (SquadShip squadShip in sameLevelShips)
            {
                int lessThan = sameLevelShips.Count(other => other.Offset.y < squadShip.Offset.y);
                int moreThan = sameLevelShips.Count(other => other.Offset.y > squadShip.Offset.y);
                if ((lessThan == count / 2 && moreThan == count / 2 - 1) ||
                    (moreThan == count / 2 && lessThan == count / 2 - 1))
                {
                    if (lessThan > moreThan && position.y > squadShip.Offset.y)
                    {
                        possibleCenter = squadShip;
                        break;
                    }
                    if (moreThan > lessThan && position.y < squadShip.Offset.y)
                    {
                        possibleCenter = squadShip;
                        break;
                    }
                    if (moreThan > lessThan && position.y > squadShip.Offset.y)
                    {
                        placeBetweenShips = true;
                    }
                }
            }

            if (possibleCenter != null)
            {
                float distance = position.y > possibleCenter.Offset.y
                    ? Mathf.Abs(bottom.Offset.y - possibleCenter.Offset.y)
                    : Mathf.Abs(top.Offset.y - possibleCenter.Offset.y);
                return new Vector2(
                    position.x,
                    possibleCenter.Offset.y + (position.y > possibleCenter.Offset.y ? distance : -distance));
            }

            if (placeBetweenShips)
            {
                return new Vector2(position.x, (bottom.Offset.y + top.Offset.y) / 2f);
            }
            return position;
        }

        private Vector2 SnapSymmetricAxis(Vector2 position, SquadShip ship, char axis)
        {
            List<SquadShip> sameLevelShips = axis == 'x'
                ? CurrentSquad.GetSquadShips().Where(squadShip => squadShip.Offset.y == ship.Offset.y).ToList()
                : CurrentSquad.GetSquadShips().Where(squadShip => squadShip.Offset.x == ship.Offset.x).ToList();

            if (sameLevelShips.Count <= 1)
            {
                return position;
            }

            return sameLevelShips.Count % 2 == 0
                ? GetEvenSymmetricPoint(sameLevelShips, position, axis)
                : GetOddSymmetricPoint(sameLevelShips, position, axis);
        }

        private static bool ShouldSnapToYAxis(Vector2 position, SquadShip ship, float tooClose)
        {
            return Mathf.Abs(ship.Offset.y - position.y) <= tooClose;
        }

        private static bool ShouldSnapToXAxis(Vector2 position, SquadShip ship, float tooClose)
        {
            return Mathf.Abs(ship.Offset.x - position.x) <= tooClose;
        }

        private void ConfigureDragStatus(Vector2 screenPosition)
        {
            _scene.DragStatusBox.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("bad");
            _scene.DragStatusBox.transform.position = screenPosition;
            _scene.DragStatusBox.GetComponent<RectTransform>().sizeDelta = ConfigData.ShipOffset;
            float scale = 4f * _workspace.VisualScale;
            _scene.DragStatusBox.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private void SetIconScreenPosition(DragIcon icon, Vector2 screenPosition)
        {
            if (icon == null)
            {
                return;
            }

            icon.GetIcon().transform.localScale = BaseIconScale(icon.GetFleetShip().Type) * _workspace.VisualScale;
            icon.SetPosition(screenPosition);
        }

        private Vector3 BaseIconScale(ConfigData.ShipTypes shipType)
        {
            Vector2 scale = ConfigData.BaseDragIconSize / ConfigData.GetShipSizeFactor(shipType);
            return new Vector3(scale.x, scale.y, 1f);
        }

        private static void PositionDeadShipBox(DragIcon icon, Vector2 screenPosition)
        {
            GameObject deadShipBox = icon?.GetDeadShipBox();
            if (deadShipBox != null)
            {
                deadShipBox.transform.position = screenPosition;
            }
        }
    }
}
