using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;

namespace Assets.Scripts.Level
{
    public class LevelInputManager
    {
        private bool _rightMouseButtonDown;
        private bool _leftMouseButtonDown;
        private bool _rightMouseButtonUp;
        private bool _leftMouseButtonUp = false;
        private bool _scrollPositive = false;
        private bool _scrollNegative = false;
        private bool _leftShift = false;
        private bool _leftControl = false;
        private bool _mouseAtTopEdge = false;
        private bool _mouseAtBottomEdge = false;
        private bool _mouseAtLeftEdge = false;  
        private bool _mouseAtRightEdge = false;
        private bool _escapeKey;
        private bool _isLeftMouseDragging = false;
        private bool _selectingPatrolArea = false;
        private bool _selectingGuardTarget = false;
        //private bool _isDragMovingSquads;
        private Vector2 _mousePosition;
        private Vector2 _mouseDownPosition;

        private Ship _clickedShip = null;

        public Selector Selector;
        public LevelStage Level;
        public const int RightClick = 1;
        public const int LeftClick = 0;
        public EventSystem EventSystem => Level.EventSystem;




        public LevelInputManager(LevelStage level, Selector selector)
        {
            Level = level;
            _mousePosition = Level.Camera.ScreenToWorldPoint(Input.mousePosition);
            Selector = selector;
        }


        public void Update()
        {
            CheckInputs();
            CheckActions();
            ResetInputs();
            //Debugger.Log($"width: {Screen.width}, height: {Screen.height}, mousePosition: {_mousePosition}");
        }
        private void ResetInputs()
        {
            _rightMouseButtonUp = false;
            _leftMouseButtonUp = false;
            _scrollPositive = false;
            _scrollNegative = false;
            _leftShift = false;
            _leftControl = false;
            _mouseAtLeftEdge = false;
            _mouseAtRightEdge = false;
            _mouseAtBottomEdge = false;
            _mouseAtTopEdge = false;
            _escapeKey = false;
            _clickedShip = null;
        }
        private void CheckInputs() {

            Vector2 mouse = Input.mousePosition;
            _mousePosition = Level.Camera.ScreenToWorldPoint(Input.mousePosition);

            if (Input.GetKey(KeyCode.LeftShift))
            {
                _leftShift = true;
            }
            else if (Input.GetKey(KeyCode.LeftControl))
            {
                _leftControl = true;
            }
            else
            {
                KeyCode[] keycodes = ConfigData.SquadKeys;
                for (int i = 0; i < keycodes.Length; i++)
                {
                    if (Input.GetKeyDown(keycodes[i]))
                    {
                        Debugger.Log("Pressed key");
                        int squadNumber = int.Parse(keycodes[i].ToString().Substring(5));

                        if (squadNumber == 0)
                        {
                            squadNumber = 10;
                        }

                        GameState state = Level.GetState();
                        int friendlySquads = state.OriginalSquadCounts[ConfigData.Configuration.UserSide-1];
                        squadNumber %= friendlySquads;
                        if (squadNumber == 0)
                        {
                            squadNumber = friendlySquads;
                        }

                        Squad squad = state.GetSquadByNumber(ConfigData.Configuration.UserSide, squadNumber);
                        if (state.GetSelectedSquads().Contains(squad))
                        {
                            //Debugger.Log("Selecting an already selected squad");
                            Vector2 position = squad.GetPosition();
                            Level.Camera.orthographicSize = Level.DefaultZoom;
                            Level.Camera.transform.localPosition = new Vector3(position.x, position.y, -10);
                            MaintainScrollBoundary();
                        }
                        else
                        {
                            //Debugger.Log("Selecting a new squad");
                            if (squad != null)
                            {
                                Vector2 position = squad.GetPosition();
                                Level.Camera.transform.localPosition = new Vector3(position.x, position.y, -10);
                                Level.Camera.orthographicSize = Level.MaxZoom;
                                MaintainScrollBoundary();
                            }

                        }
                         //Debugger.Log("Pressed " + squadNumber);
                        state.SelectSquad(squad);
                    }
                }
            }

            if (Input.GetMouseButtonDown(LeftClick)) // left mouse button down
            {
                Debugger.Log("Pressed Lefft mouse button");
                if (EventSystem.IsPointerOverGameObject())
                {
                    CheckForMiniMapNavigation(LeftClick);
                    return;
                };
                _leftMouseButtonDown = true;
                _leftMouseButtonUp = false;
                _mouseDownPosition = new Vector2(_mousePosition.x, _mousePosition.y);
            }
            else if (Input.GetMouseButtonUp(LeftClick))
            {
                if (EventSystem.IsPointerOverGameObject()) return;

                _leftMouseButtonUp = true;
                _leftMouseButtonDown = false;
            }
            else if (Input.GetMouseButtonDown(RightClick)) // right mouse button down
            {
                if (EventSystem.IsPointerOverGameObject())
                {
                    CheckForMiniMapNavigation(RightClick);
                    return;
                }
                _rightMouseButtonUp = false;
                _rightMouseButtonDown = true;
            }
            else if (Input.GetMouseButtonUp(RightClick)) // right mouse button up
            {
                if (EventSystem.IsPointerOverGameObject()) return;
                _rightMouseButtonUp = true;
                _rightMouseButtonDown = false;
            }

            if (Input.GetAxis("Mouse ScrollWheel") > 0)
            {
                _scrollPositive = true;
            }
            else if (Input.GetAxis("Mouse ScrollWheel") < 0)
            {
                _scrollNegative = true;
            }

            if (mouse.x < Utilities.WorldUnitsToScreenPixels(Level.MouseScrollDistanceFromEdge, Level.Camera).x)   
            {
                _mouseAtLeftEdge = true;
            }else if (mouse.x >= ConfigData.ScreenWidth - Utilities.WorldUnitsToScreenPixels(Level.MouseScrollDistanceFromEdge, Level.Camera).x)
            {
                _mouseAtRightEdge = true;
            }

            if (mouse.y < Utilities.WorldUnitsToScreenPixels(Level.MouseScrollDistanceFromEdge, Level.Camera).y)
            {
                _mouseAtBottomEdge = true;
            }else if (mouse.y >= ConfigData.ScreenHeight - Utilities.WorldUnitsToScreenPixels(Level.MouseScrollDistanceFromEdge, Level.Camera).y)
            {
                _mouseAtTopEdge = true;
            }

            if (Input.GetMouseButton(LeftClick))
            {
                if (EventSystem.IsPointerOverGameObject()) return;
                if (Input.GetAxis("Mouse X") != 0 || Input.GetAxis("Mouse Y") != 0)
                {
                    _isLeftMouseDragging = true; 
                }

            }
            

        }


        private bool HasOpenMenuInput()
        {
            return Input.GetKey(KeyCode.Escape);
        }
        private bool HasDragMoveSquadsInput()
        {
            if (EventSystem.IsPointerOverGameObject())
            {
                return false;
            }
            return Input.GetMouseButton(RightClick);
        }
        private bool HasMoveSquadsInput()
        {
            if (EventSystem.IsPointerOverGameObject())
            {
                return false;
            }
            return Input.GetMouseButtonUp(RightClick);
        }
        private bool HasSelectingGuardShipInput()
        {
            if (Input.GetMouseButtonUp(RightClick))
            {
                if (_clickedShip != null)
                {
                    if (_clickedShip.IsUserControlled)
                    {
                        
                        return true;
                    }
                }
            }
            return false;
        }
        private bool HasAttackingShipInput()
        {
            if (Input.GetMouseButtonUp(RightClick))
            {
                if (_clickedShip != null)
                {
                    if (!_clickedShip.IsUserControlled)
                    {
                        return true;
                    }
                }
                else if (CheckForAttackAISquad())
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The new logic is as follows:
        /// 1. As many inputs will be able to be customized in settings, it's important to abstract the input logic for an action away from the specific keys
        /// 2. Check each action individually to see if it's been triggered
        /// </summary>
        private void CheckActions()
        {
            if (HasOpenMenuInput())
            {
                Level.Menus.OpenMenu();
            }
            if (!Level.IsPaused)
            {
                //Debugger.Log($"EVS: {EventSystem.IsPointerOverGameObject()}");
                CheckClickCollision();
                if (HasDragMoveSquadsInput())
                {
                    //Debugger.Log("Has drag input");
                    MoveSquads(Level.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (HasSelectingGuardShipInput())
                {
                    CheckForSelectingGuard(_clickedShip);
                }
                else if (HasAttackingShipInput())
                {
                    _clickedShip.Clicked(RightClick);
                }
                else if (HasMoveSquadsInput())
                {
                    //Debugger.Log("Has move input");
                    MoveSquads(Level.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (_leftMouseButtonUp)
                {
                    //Debugger.Log($"Left mouse button up: is left mouse dragging? {_isLeftMouseDragging}");
                    if (_isLeftMouseDragging && !_selectingPatrolArea)
                    {
                        Selector.SelectShipsInBox();
                    }
                    else if (!LeftClickAction())
                    {
                        Selector.SelectShipsInBox();
                    }
                    _isLeftMouseDragging = false;
                    //Debugger.Log("Deactivated box");
                    Selector.Deactivate();

                }
                if (_isLeftMouseDragging)
                {

                    Selector.DrawSelectionBox(_mouseDownPosition, _mousePosition);
                }

                if (_scrollPositive)
                {
                    if (_leftShift)
                    {
                        ScrollLeft();
                    }
                    else if (_leftControl)
                    {
                        ZoomIn();
                    }
                    else
                    {
                        ScrollUp();
                    }
                }
                else if (_scrollNegative)
                {
                    if (_leftShift)
                    {
                        ScrollRight();
                    }
                    else if (_leftControl)
                    {
                        ZoomOut();
                    }
                    else
                    {
                        ScrollDown();
                    }
                }

                float scrollSpeed = Level.ScrollSpeed * 2.5f * Time.deltaTime;

                if (Level.UseMouseScrolling)
                {
                    if (_mouseAtLeftEdge)
                    {
                        ScrollLeft(scrollSpeed);

                    }
                    else if (_mouseAtRightEdge)
                    {
                        ScrollRight(scrollSpeed);
                    }

                    if (_mouseAtTopEdge)
                    {
                        ScrollUp(scrollSpeed);

                    }
                    else if (_mouseAtBottomEdge)
                    {
                        ScrollDown(scrollSpeed);
                    }
                }
                
            }

        }
        
        public Vector2 GetMousePosition()
        {
            return _mousePosition;
        }
        public void SetPatrolAreaActive()
        {
            _selectingPatrolArea = true;
        }
        public void SetSelectGuardTargetActive()
        {
            _selectingGuardTarget = true;
        }


        private void CheckForSelectingGuard(Ship ship)
        {
            if ( _selectingGuardTarget) // if we're set to select a guard target
            {
                Debugger.Log($"Selecting {ship.name} for guarding");
                Level.GetState().GetSelectedSquads().ForEach((squad) =>
                {
                    squad.UserGuard(ship.Squad); // make all selected ships guard this squad
                });
                _selectingGuardTarget = false;
            }
        }
        private bool CheckForSelectingSquad()
        {
            //Debugger.Log("Didn't click on a ship, looking for nearby ships");
            GameState state = Level.GetState();
            List<Ship> ships = state.GetShips(ConfigData.Configuration.UserSide);
            Squad potentialSquad = null;
            Vector2 levelPosition = _mousePosition - (Vector2)Level.transform.position;

            foreach (Ship ship in ships)
            {
                if (ship.DistanceToPoint(levelPosition) <= 5)
                {
                    potentialSquad = ship.Squad;
                    _clickedShip = ship;
                }
            }

            if (potentialSquad != null)
            {
                state.SelectSquad(potentialSquad);
                //Debugger.Log($"Mouse was close enough to ${potentialSquad.squadNumber}");
                return true;
            }
            return false;
        }
        private bool CheckForAttackAISquad()
        {
            GameState state = Level.GetState();
            List<Ship> ships = state.GetShips(ConfigData.Configuration.AISide);
            Squad potentialSquad = null;
            Vector2 levelPosition = _mousePosition - (Vector2)Level.transform.position;

            foreach (Ship ship in ships)
            {
                if (ship.DistanceToPoint(levelPosition) <= 10)
                {
                    //Debugger.Log($"Targeting a potential AI squad! {ship.Squad.Name}");
                    potentialSquad = ship.Squad;
                    _clickedShip = ship;

                }
            }
            if (potentialSquad != null)
            {
                potentialSquad.GetShips().First().Clicked(RightClick);
                //Debugger.Log($"Mouse was close enough to ${potentialSquad.Name}");
                return true;
            }
            return false;
        }
        private void MoveSquads(Vector2 targetPosition)
        {
            List<Squad> squads = Level.GetState().GetSelectedSquads();
            squads.ForEach((squad) =>
            {
                Vector2 localized = targetPosition - (Vector2) Level.transform.position;
                //Debugger.Log($"Squad: {squad.Name} World point target position: {targetPosition}, localized: {localized}");

                //float x = Mathf.Clamp(targetPosition.x, Level.MinX, Level.MaxX);
                //float y = Mathf.Clamp(targetPosition.y, Level.MinY, Level.MaxY);

                //targetPosition = new Vector2(x, y);

                // if the user is controlling this squad and setting it to target an enemy, end that.
                squad.FinalizeUserCommand();
                squad.Move(localized);
            });
        }
        private bool CheckForSelectingPatrolArea()
        {
            //Debugger.Log($"Checking to select for patrol area: {_selectingPatrolArea}");
            if (_selectingPatrolArea)
            {
                GameState state = Level.GetState();
                state.GetSelectedSquads().ForEach((squad) =>
                {
                    Vector2 startingPosition = _mouseDownPosition - (Vector2)Level.transform.position;
                    Vector2 endingPosition = _mousePosition - (Vector2)Level.transform.position;
                    squad.UserPatrol(startingPosition, endingPosition);
                });
                _selectingPatrolArea = false;
                return true;
            }
            return false;
        }
        private Ship CheckClickCollision()
        {
            Vector3 mousePos = Level.Camera.ScreenToWorldPoint(Input.mousePosition);
            Vector2 mousePos2D = new Vector2(mousePos.x, mousePos.y);

            RaycastHit2D hit = Physics2D.Raycast(mousePos2D, Vector2.zero);

            if (hit.collider != null && hit.collider.CompareTag("Ship"))
            {
                //Debugger.Log(hit.collider.gameObject.name);
                Ship ship = (Ship)hit.collider.gameObject.GetComponent(typeof(Ship));
                _clickedShip = ship;
                return ship;
            }
            else
            {
                //Debugger.Log($"Did not hit any ship {hit}");
            }
            return null;
        }
        private void CheckForMiniMapNavigation(int mouseButton)
        {
            if (!Level.Menus.HoveringOverMiniMapButton)
            {
                PointerEventData eventData = new PointerEventData(EventSystem.current);
                eventData.position = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                //Debugger.Log($"Raycasting from {eventData.position}");


                List<RaycastResult> results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(eventData, results);

                foreach (RaycastResult hit in results)
                {
                    //Debugger.Log($"This raycast hit {hit.gameObject.name}");
                    if (hit.gameObject.name == "Camera Collider")
                    {
                        Vector2 miniMapPoint = hit.gameObject.transform.InverseTransformPoint(hit.screenPosition);
                        Vector2 viewPortPoint = miniMapPoint + new Vector2(.5f, .5f);
                        Vector2 viewPortWorldPoint = Level.MiniMapCamera.ViewportToWorldPoint(viewPortPoint);
                        Vector2 localized = viewPortWorldPoint - (Vector2)Level.transform.position;

                        //Debugger.Log($"Hit: Screen position: {hit.screenPosition}, Mini Map position: {miniMapPoint}, View port position: {viewPortPoint}, Viewport World Point: {viewPortWorldPoint}," +
                        //    $"Localized: {localized} ");

                        if (mouseButton == RightClick)
                        {
                            MoveSquads(viewPortWorldPoint);
                        }
                        else
                        {
                            Level.Camera.transform.localPosition = new Vector3(localized.x, localized.y, -10);
                            MaintainScrollBoundary();
                        }

                    }

                }
            }
            
        }



        private bool LeftClickAction()
        {
            if (_clickedShip != null)
            {
                _clickedShip.Clicked(LeftClick);
                return true;
            }
            else
            {
                if (!CheckForSelectingSquad())
                {
                    if (!CheckForSelectingPatrolArea()) {
                        return false;
                    }
                }
                return true;

            }
        }
        



        // scrolling methods
        private void ScrollRight(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Level.ScrollSpeed;
            }
            Vector3 position = Level.Camera.transform.position;
            Level.Camera.transform.position = new Vector3(position.x + scrollSpeed, position.y, -10);
            MaintainScrollBoundary();

        }
        private void ScrollLeft(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Level.ScrollSpeed;
            }
            Vector3 position = Level.Camera.transform.position;
            Level.Camera.transform.position = new Vector3(position.x - scrollSpeed, position.y, -10);
            MaintainScrollBoundary();

        }
        private void ScrollUp(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Level.ScrollSpeed;
            }
            Vector3 position = Level.Camera.transform.position;
            Level.Camera.transform.position = new Vector3(position.x, position.y + scrollSpeed, -10);
            MaintainScrollBoundary();

        }
        private void ScrollDown(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Level.ScrollSpeed;
            }
            Vector3 position = Level.Camera.transform.position;
            Level.Camera.transform.position = new Vector3(position.x, position.y - scrollSpeed, -10);
            MaintainScrollBoundary();

        }
        private void ZoomIn()
        {
            float difference = -1 * Level.ZoomSpeed;
            if ((Level.Camera.orthographicSize + difference) < Level.MinZoom)
            {
                difference = Level.Camera.orthographicSize - Level.MinZoom;
            }
            Level.Camera.orthographicSize += difference; // orthographic size decreases, zooming in

            MaintainScrollBoundary();
        }
        private void ZoomOut()
        {
            float difference = Level.ZoomSpeed;
            if ((Level.Camera.orthographicSize + difference) > Level.MaxZoom)
            {
                difference = Level.Camera.orthographicSize - Level.MaxZoom;
            }
            Level.Camera.orthographicSize += difference; // orthographic size increases, zooming out

            MaintainScrollBoundary();
        }
        public void MaintainScrollBoundary()
        {
            //Level.MiniMapCamera.transform.position = new Vector3(0, 0, -10);
            MaintainHorizontalScrollBoundary(Level.Camera);
            MaintainHorizontalScrollBoundary(Level.MiniMapCamera);
            MaintainVerticalScrollBoundary(Level.Camera);
            MaintainVerticalScrollBoundary(Level.MiniMapCamera);
        }
        private void MaintainHorizontalScrollBoundary(Camera camera)
        {
            // make sure the horizontal scroll isn't out of bounds
            Vector3 position = camera.transform.position;

            float camVertExtent = camera.orthographicSize;
            float camHorzExtent = camera.aspect * camVertExtent;
            Bounds mapBounds = Level.MapRenderer.bounds;

            float leftBound = mapBounds.min.x + camHorzExtent;
            float rightBound = mapBounds.max.x - camHorzExtent;

            float camX = Mathf.Clamp(position.x, leftBound, rightBound);

            camera.transform.position = new Vector3(camX, position.y, position.z);
        }
        private void MaintainVerticalScrollBoundary(Camera camera)
        {
            // make sure the vertical scroll isn't out of bounds
            Vector3 position = camera.transform.position;

            float camVertExtent = camera.orthographicSize;
            Bounds mapBounds = Level.MapRenderer.bounds;

           
            float bottomBound = mapBounds.min.y + camVertExtent;
            float topBound = mapBounds.max.y - camVertExtent;

            float camY = Mathf.Clamp(position.y, bottomBound, topBound); 

            //Debugger.Log($"mapBoundSize: {mapBounds.size}, mapBoundMax: {mapBounds.max}, camPosition: {position}, camVertExtent: {camVertExtent}, bottomBound: {bottomBound}, topBound: {topBound}, camY: {camY}");

            camera.transform.position = new Vector3(position.x, camY, position.z);

        }
    }
}