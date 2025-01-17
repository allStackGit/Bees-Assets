using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
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
        /// <summary>
        /// Whether or not the left mouse button has been pressed down, instead of return false the next frame it returns false after a short delay so that double clicks can be registered
        /// </summary>
        private bool _leftMouseClicked;
        private float _leftMouseClickedTime;
        private bool _leftMouseDoubleClicked;
        private bool _rightMouseButtonUp;
        private bool _leftMouseButtonUp;
        private bool _scrollPositive;
        private bool _scrollNegative;
        private bool _leftShift;
        private bool _leftControl;
        private bool _mouseAtTopEdge;
        private bool _mouseAtBottomEdge;
        private bool _mouseAtLeftEdge;  
        private bool _mouseAtRightEdge;
        private bool _isLeftMouseDragging;
        private bool _selectingPatrolArea;
        private bool _selectingGuardTarget;
        private bool _isRightMouseDownPrior;
        private bool _isRightMouseDragging;
        private bool _isDragMovementBlockedByTimer;

        private List<HotKey> _hotKeys;

        //private bool _isDragMovingSquads;
        private Vector2 _mousePosition;
        private Vector2 _mouseDownPosition;
        private Vector2 _previousMousePosition;
        private Vector2 _previousDragMousePosition;
        //private float _timeSinceMiniMapToggled;

        private Ship _clickedShip = null;
        private MiningAsteroid _clickedMiningAsteroid = null;

        public Selector Selector;
        public LevelStage Level;
        public const int RightClick = 1;
        public const int LeftClick = 0;
        public List<Timer> Timers = new List<Timer>();
        public List<Turret> TurretsFiringManually = new List<Turret>();
        public EventSystem EventSystem => Level.EventSystem;

        // these are booleans for user inputs that are held down so that we don't need to fire the action the entire time they're held down

        public bool IsShowingRanges;
        public bool IsFiringManually;

        


        public LevelInputManager(LevelStage level, Selector selector)
        {
            Level = level;
            _mousePosition = Level.Camera.ScreenToWorldPoint(Input.mousePosition);
            Selector = selector;

            LoadHotKeySettings();
        }

        public void LoadHotKeySettings()
        {
            _hotKeys = ConfigData.GetUserSettingsData().HotKeys;

            // make special input for firing and showing ranges together
            //List<KeyCode> combinedKeys = ConfigData.GetUserSettingsData().FindKey("Show Ranges").Keys.ToList();
            //combinedKeys.AddRange(ConfigData.GetUserSettingsData().FindKey("Manual Fire").Keys);
            //_hotKeys.Add(new HotKey("Show Ranges and Manual Fire", combinedKeys, () =>
            //{
            //    Debug.Log("Double action");
            //    ShowRanges();
            //    ManualFire();
            //}, () =>
            //{
            //    Debug.Log("Double release action");
            //    ShowRanges();
            //    ManualFire();
            //}, false, true));


            _hotKeys.ForEach((hotKey =>
            {
                switch (hotKey.Name)
                {
                    case "Match Speed":
                        //Debug.Log($"Found match speed: {hotKey}");
                        hotKey.SetAction(() =>
                        {
                            //Debug.Log($"Match speed action!");
                            Level.Menus.ActionBox.MatchSpeed();
                        });
                        break;
                    case "Attack on Sight":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.AttackOnSight();
                        });
                        break;
                    case "Cease Fire":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.CeaseFire();
                        });
                        break;
                    case "Patrol":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Patrol();
                        });
                        break;
                    case "Guard":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Guard();
                        });
                        break;
                    case "Chase":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Chase();
                        });
                        break;
                    case "Hold":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Hold();
                        });
                        break;
                    case "Detonate":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Detonate();
                        });
                        break;
                    case "Charge":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.Charge();
                        });
                        break;
                    case "Drop Beacon":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.DropBeacon();
                        });
                        break;

                    case var _ when ConfigData.Configuration.ShootingStrategies.Contains(hotKey.Name):
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ActionBox.SetShootingStrategy(hotKey.Name);
                        });
                        break;

                    case var _ when hotKey.Name.StartsWith("Select Squad #"):
                        int number = int.Parse(hotKey.Name.Substring(hotKey.Name.Length - 1));
                        hotKey.SetAction(() =>
                        {
                            SelectSquadByNumber(number);
                        });
                        break;

                    case "Open Menu":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.OpenMenu();
                            hotKey.ManuallySetInputRelease(true);
                        });
                        break;
                    case "Show Ranges":
                        hotKey.SetAction(() =>
                        {
                            if (!IsShowingRanges)
                            {
                                //Debug.Log("Show ranges");
                                ShowRanges();
                            }

                        });
                        hotKey.SetReleaseAction(() =>
                        {
                            if (IsShowingRanges)
                            {
                                //Debug.Log("Show ranges release");
                                ShowRanges();
                            }

                        });
                        break;
                    case "Manual Fire":
                        hotKey.SetAction(() =>
                        {
                            if (!IsFiringManually)
                            {
                                //Debug.Log("Manual fire");
                                ManualFire();
                            }

                        });
                        hotKey.SetReleaseAction(() =>
                        {
                            if (IsFiringManually)
                            {
                                //Debug.Log("Manual fire release");
                                ManualFire();
                            }
                        });
                        break;
                    case "Show Ranges + Manual Fire":
                        hotKey.SetAction(() =>
                        {
                            //Debug.Log("Show Ranges + Manual Fire");
                            if (!IsShowingRanges)
                            {
                                ShowRanges();
                            }
                            if (!IsFiringManually)
                            {
                                ManualFire();
                            }
                        });
                        hotKey.SetReleaseAction(() =>
                        {
                            //Debug.Log("Show Ranges + Manual Fire release");
                            ConfigData.GetUserSettingsData().FindKey("Show Ranges").ManuallySetInputRelease(true);
                            ConfigData.GetUserSettingsData().FindKey("Manual Fire").ManuallySetInputRelease(true);
                            if (IsShowingRanges && !ConfigData.GetUserSettingsData().FindKey("Show Ranges").HasInput())
                            {
                                ShowRanges();
                            }
                            if (IsFiringManually && !ConfigData.GetUserSettingsData().FindKey("Manual Fire").HasInput())
                            {
                                ManualFire();
                            }
                        });
                        break;
                    case "Toggle Mini Map":
                        hotKey.SetAction(() =>
                        {
                            Level.Menus.ToggleMiniMapDisplay();
                        });
                        break;
                    case "Move Camera Up":
                        hotKey.SetAction(() =>
                        {
                            ScrollUp();
                        });
                        break;
                    case "Move Camera Right":
                        hotKey.SetAction(() =>
                        {
                            ScrollRight();
                        });
                        break;
                    case "Move Camera Down":
                        hotKey.SetAction(() =>
                        {
                            ScrollDown();
                        });
                        break;
                    case "Move Camera Left":
                        hotKey.SetAction(() =>
                        {
                            ScrollLeft();
                        });
                        break;

                }
            }));
        }
        public void ShowRanges()
        {
            if (!IsShowingRanges)
            {
                Level.GetState().GetSelectedSquads().ForEach(s => {
                    if (!s.IsShowingRanges)
                    {
                        s.ShowSquadRanges();
                    }
                });
                IsShowingRanges = true;
            }
            else
            {
                Level.GetState().GetSelectedSquads().ForEach(s => {
                    if (s.IsShowingRanges)
                    {
                        s.HideSquadRanges();
                    }
                });
                IsShowingRanges = false;
            }
        }
        public void ManualFire()
        {
            if (!IsFiringManually)
            {
                Level.GetState().GetSelectedSquads().ForEach(squad => {
                    squad.GetShips().ForEach((ship) =>
                    {
                        ship.Turrets.ForEach((turret) =>
                        {
                            turret.IsFiringManually = true;
                            TurretsFiringManually.Add(turret);
                        });
                        if (ship.ShipType == "Flagship")
                        {
                            ship.StopMoving("Flagship is manually firing");
                        }
                    });
                });
                IsFiringManually = true;
            }
            else
            {
                TurretsFiringManually.ForEach((turret) =>
                {
                    turret.IsFiringManually = false;
                });
                IsFiringManually = false;
            }
        }
        public void SelectSquadByNumber(int squadNumber)
        {
            if (squadNumber == 0)
            {
                squadNumber = 10;
            }

            GameState state = Level.GetState();
            int friendlySquads = state.OriginalSquadCounts[ConfigData.Configuration.UserSide - 1];
            squadNumber %= friendlySquads;
            if (squadNumber == 0)
            {
                squadNumber = friendlySquads;
            }

            Squad squad = state.GetSquadByNumber(ConfigData.Configuration.UserSide, squadNumber);
            if (squad != null)
            {
                Vector2 position = squad.GetPosition();
                Level.Camera.transform.position = new Vector3(position.x, position.y, -10) + Level.Get3DPosition();
                //ToggleZoom();
                MaintainScrollBoundary();
            }
            //Debug.Log("Pressed " + squadNumber);
            state.SelectSquad(squad);
        }
        public void Update()
        {
            CheckInputs();
            CheckActions();
            ResetInputs();
            for (int i = 0; i < Timers.Count; i++)
            {
                if (Timers[i].Update())
                {
                    Timers.RemoveAt(i);
                }
            }
            //Debug.Log($"width: {Screen.width}, height: {Screen.height}, mousePosition: {_mousePosition}");
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
            _clickedShip = null;
            _clickedMiningAsteroid = null;
        }
        private void CheckInputs() {

            _previousMousePosition = _mousePosition;
            Vector2 mouse = Input.mousePosition;
            _mousePosition = Level.Camera.ScreenToWorldPoint(mouse);

            // A test to see which keys are being pressed
            //if (Input.anyKeyDown)
            //{
            //    foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            //    {
            //        if (Input.GetKeyDown(key))
            //        {
            //            Debug.Log("Key pressed: " + key);
            //        }
            //    }
            //}

            if (Input.GetKey(KeyCode.LeftShift))
            {
                _leftShift = true;
            }
            else if (Input.GetKey(KeyCode.LeftControl))
            {
                _leftControl = true;
            }

            if (Input.GetMouseButtonDown(LeftClick)) // left mouse button down
            {
                //Debug.Log("Pressed Left mouse button");
                if (EventSystem.IsPointerOverGameObject())
                {
                    CheckForMiniMapNavigation(LeftClick);
                    return;
                };
                _leftMouseButtonDown = true;
                _leftMouseButtonUp = false;
                _mouseDownPosition = new Vector2(_mousePosition.x, _mousePosition.y);
                if (_leftMouseClicked)
                {
                    _leftMouseDoubleClicked = true;
                }
                _leftMouseClicked = true;
                _leftMouseClickedTime = Time.realtimeSinceStartup;
            }
            else if (Input.GetMouseButtonUp(LeftClick))
            {
                if (EventSystem.IsPointerOverGameObject() && !_isLeftMouseDragging)
                {
                    return;
                }

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
                _isRightMouseDownPrior = true;
                Timers.Add(new Timer(.25f, SetRightMouseDownLongEnoughForDragging));
            }
            else if (Input.GetMouseButtonUp(RightClick)) // right mouse button up
            {
                if (EventSystem.IsPointerOverGameObject()) return;
                _rightMouseButtonUp = true;
                _rightMouseButtonDown = false;
                _isRightMouseDownPrior = false;
                _isRightMouseDragging = false;
                //Debug.Log("Drag move mouse down prior is being set to false because right mouse button went up");
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
                if (Mathf.Abs(Input.GetAxis("Mouse X")) > .5f || Mathf.Abs(Input.GetAxis("Mouse Y")) > .5f)
                {
                    //Debug.Log($"Mouse Axis: {Input.GetAxis("Mouse X")}, {Input.GetAxis("Mouse Y")}");
                    _isLeftMouseDragging = true; 
                }

            }

            if (Time.realtimeSinceStartup - _leftMouseClickedTime > .25f)
            {
                _leftMouseClicked = false;
                _leftMouseDoubleClicked = false;
            }
            

        }
        private void ToggleZoom()
        {
            if (Level.Camera.orthographicSize == Level.Map.MaxZoom)
            {
                Level.Camera.orthographicSize = Level.DefaultZoom;
            }
            else
            {
                Level.Camera.orthographicSize = Level.Map.MaxZoom;
            }
        }
        private void SetRightMouseDownLongEnoughForDragging()
        {
            if (_isRightMouseDownPrior)
            {
                _isRightMouseDragging = true;
            }
        }
        private bool HasDragMoveSquadsInput()
        {
            if (!_isDragMovementBlockedByTimer && !EventSystem.IsPointerOverGameObject() && _isRightMouseDragging && Vector2.Distance(_previousDragMousePosition, _mousePosition) > 5)
            {
                //Debug.Log($"Drag moving");
                _isDragMovementBlockedByTimer = true;
                _previousDragMousePosition = _mousePosition;
                Timers.Add(new Timer(.25f, UnblockDragMovement));
                return Input.GetMouseButton(RightClick);
            }
            return false;
        }
        private void UnblockDragMovement()
        {
            _isDragMovementBlockedByTimer = false;
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
            return _selectingGuardTarget && Input.GetMouseButtonUp(RightClick) && _clickedShip != null && _clickedShip.IsUserControlled;
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
        private bool HasMiningCommandInput()
        {
            if (_rightMouseButtonUp)
            {
                //Debug.Log($"right up: {_rightMouseButtonUp}");
                RaycastHit2D[] hits = Physics2D.RaycastAll(Level.Camera.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                for (int i = 0; i < hits.Length; i++)
                {
                    RaycastHit2D hit = hits[i];
                    if (hit.collider != null && hit.collider.CompareTag("Mining Asteroid"))
                    {
                        //Debug.Log($"hit: {hit.collider.gameObject.name}");
                        _clickedMiningAsteroid = hit.collider.gameObject.GetComponent<MiningAsteroid>();
                        return true;
                    }
                }
            }
            return false;
        }
        private bool HasFullRetreatCommandInput()
        {
            //Debug.Log($"right up: {Input.GetMouseButtonUp(RightClick)}, _clickedShip: {_clickedShip?.Name}");
            return Input.GetMouseButtonUp(RightClick) && _clickedShip != null && _clickedShip.IsWarpGate;
        }
        private bool HasEitherControlKey()
        {
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }
        public bool HasPauseInput()
        {
            return Input.GetKey(KeyCode.P);
        }

        /// <summary>
        /// The new logic is as follows:
        /// 1. As many inputs will be able to be customized in settings, it's important to abstract the input logic for an action away from the specific keys
        /// 2. Check each action individually to see if it's been triggered
        /// </summary>
        private void CheckActions()
        {
            //List<KeyCode> keysPressed = Utilities.GetAllKeys();
            //Debug.Log($"Keys pressed: {Utilities.ListToString(keysPressed)}");
            foreach (HotKey hotKey in _hotKeys)
            {
                //if (hotKey.Name == "Match Speed")
                //{
                //    Debug.Log($"Checking Match Speed: {hotKey}");
                //}
                if (hotKey.CheckInput())
                {
                    //Debug.Log($"Input registered from {hotKey}");
                }
            }
            if (HasPauseInput())
            {
                if (!Level.IsPaused && Time.realtimeSinceStartup - Level.TimePaused > 1)
                {
                    Level.IsPausedByTester = true;
                    Level.TimePaused = Time.realtimeSinceStartup;
                    Level.Pause();
                    Debug.Break();
                    return;
                }
            }
            
            if (!Level.IsPaused)
            {
                //Debug.Log($"EVS: {EventSystem.IsPointerOverGameObject()}");
                CheckClickCollision();
                //Debug.Log($"Frame: {Level.Updates}, clicked ship: {_clickedShip}");
                if (HasDragMoveSquadsInput())
                {
                    //Debug.Log("Has drag input");
                    MoveSquads(Level.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (HasSelectingGuardShipInput())
                {
                    //Debug.Log("Has input for selecting guard ship");
                    SetSelectingGuard(_clickedShip);
                }
                else if (HasAttackingShipInput())
                {
                    _clickedShip.Clicked(RightClick);
                }
                else if (HasFullRetreatCommandInput())
                {
                    SetSquadsToFullRetreat((WarpGate)_clickedShip);
                }
                else if (HasMiningCommandInput())
                {
                    //Debug.Log($"Setting squads to mine");
                    SetSquadsToMine(_clickedMiningAsteroid);
                }
                else if (HasMoveSquadsInput())
                {
                    //Debug.Log("Has move input");
                    MoveSquads(Level.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (_leftMouseButtonUp)
                {
                    //Debug.Log($"Left mouse button up: clicked ship? {_clickedShip}");
                    if (_isLeftMouseDragging && !_selectingPatrolArea)
                    {
                        //Debug.Log("_leftMouseDragging");
                        Selector.SelectShipsInBox();
                    }
                    else if (!LeftClickAction())
                    {
                        Selector.SelectShipsInBox();
                    }
                    _isLeftMouseDragging = false;
                    //Debug.Log("Deactivated box");
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

                        ScrollUp();
                    }
                    else
                    {
                        ZoomIn();
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
                        ScrollDown();
                    }
                    else
                    {
                        ZoomOut();
                    }
                }

                float mouseScrollSpeed = Level.ScrollSpeed * 5 * Time.deltaTime;

                if (Level.UseMouseScrolling)
                {
                    if (_mouseAtLeftEdge)
                    {
                        ScrollLeft(mouseScrollSpeed);

                    }
                    else if (_mouseAtRightEdge)
                    {
                        ScrollRight(mouseScrollSpeed);
                    }

                    if (_mouseAtTopEdge)
                    {
                        ScrollUp(mouseScrollSpeed);

                    }
                    else if (_mouseAtBottomEdge)
                    {
                        ScrollDown(mouseScrollSpeed);
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
            //Debug.Log("Selecting guard target is active");
            _selectingGuardTarget = true;
        }


        private void SetSelectingGuard(Ship ship)
        {
            //Debug.Log($"Selecting {ship.name} for guarding");
            Level.GetState().GetSelectedSquads().ForEach((squad) =>
            {
                squad.UserGuard(ship.Squad); // make all selected ships guard this squad
            });
            _selectingGuardTarget = false;
        }
        private bool CheckForSelectingSquad()
        {
            //Debug.Log("Didn't click on a ship, looking for nearby ships");
            GameState state = Level.GetState();
            List<Ship> ships = state.GetShips(ConfigData.Configuration.UserSide);
            Squad potentialSquad = null;
            Vector2 levelPosition = _mousePosition - Level.GetPosition();

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
                if (HasEitherControlKey())
                {
                    state.AddSelectedSquad(potentialSquad);
                }
                else if (_leftMouseDoubleClicked)
                {
                    Level.GetState().SelectSquadsByShipType(_clickedShip.ShipType);
                }
                else
                {
                    state.SelectSquad(potentialSquad);
                }
                //Debug.Log($"Mouse was close enough to ${potentialSquad.Name}");
                return true;
            }
            return false;
        }
        private bool CheckForAttackAISquad()
        {
            GameState state = Level.GetState();
            List<Ship> ships = state.GetShips(ConfigData.Configuration.AISide);
            Squad potentialSquad = null;
            Vector2 levelPosition = _mousePosition - Level.GetPosition();

            foreach (Ship ship in ships)
            {
                if (ship.DistanceToPoint(levelPosition) <= 10)
                {
                    //Debug.Log($"Targeting a potential AI squad! {ship.Squad.Name}");
                    potentialSquad = ship.Squad;
                    _clickedShip = ship;

                }
            }
            if (potentialSquad != null)
            {
                potentialSquad.GetShips().First().Clicked(RightClick);
                //Debug.Log($"Mouse was close enough to ${potentialSquad.Name}");
                return true;
            }
            return false;
        }
        private void MoveSquads(Vector2 targetPosition)
        {
            Level.GetState().GetSelectedSquads().ForEach((squad) =>
            {
                Vector2 localized = targetPosition - Level.GetPosition();
                //Debug.Log($"Squad: {squad.Name} World point target position: {targetPosition}, localized: {localized}");

                //float x = Mathf.Clamp(targetPosition.x, Level.MinX, Level.MaxX);
                //float y = Mathf.Clamp(targetPosition.y, Level.MinY, Level.MaxY);

                //targetPosition = new Vector2(x, y);

                // if the user is controlling this squad and setting it to target an enemy, end that.
                if (squad.FinalizeUserCommand())
                {
                    squad.Move(localized);
                }
                
            });
        }
        private void SetSquadsToMine(MiningAsteroid asteroid)
        {
            Level.GetState().GetSelectedSquads().ForEach((squad) =>
            {
                squad.UserMining(asteroid);
            });
        }
        private void SetSquadsToFullRetreat(WarpGate warpGate)
        {
            Level.GetState().GetSelectedSquads().ForEach((squad) =>
            {
                if (squad.GetShips().Any((s) => s.ShipType != "Warp Gate"))
                {
                    squad.UserFullRetreat(warpGate);
                }
            });
        }
        private bool CheckForSelectingPatrolArea()
        {
            //Debug.Log($"Checking to select for patrol area: {_selectingPatrolArea}");
            if (_selectingPatrolArea)
            {
                GameState state = Level.GetState();
                state.GetSelectedSquads().ForEach((squad) =>
                {
                    Vector2 startingPosition = _mouseDownPosition - Level.GetPosition();
                    Vector2 endingPosition = _mousePosition - Level.GetPosition();
                    squad.UserPatrol(startingPosition, endingPosition);
                });
                _selectingPatrolArea = false;
                return true;
            }
            return false;
        }
        private void CheckClickCollision()
        {
            if (_leftMouseButtonUp || _rightMouseButtonUp)
            {
                RaycastHit2D[] hits = Physics2D.RaycastAll(Level.Camera.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                for (int i = 0; i < hits.Length; i++)
                {
                    RaycastHit2D hit = hits[i];
                    if (hit.collider != null && hit.collider.CompareTag("Ship"))
                    {
                        //Debug.Log($"hit: {hit.collider.gameObject.name}");
                        Ship ship = hit.collider.gameObject.GetComponent<Ship>();
                        _clickedShip = ship;
                    }
                    else
                    {
                        //Debug.Log($"Did not hit any ship {hit}");
                    }
                }
            }
        }
        private void CheckForMiniMapNavigation(int mouseButton)
        {
            if (!Level.Menus.HoveringOverMiniMapButton)
            {
                PointerEventData eventData = new PointerEventData(EventSystem.current);
                eventData.position = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                //Debug.Log($"Raycasting from {eventData.position}");


                List<RaycastResult> results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(eventData, results);

                foreach (RaycastResult hit in results)
                {
                    //Debug.Log($"This raycast hit {hit.gameObject.name}");
                    if (hit.gameObject.name == "Camera Collider")
                    {
                        Vector2 miniMapPoint = hit.gameObject.transform.InverseTransformPoint(hit.screenPosition);
                        Vector2 viewPortPoint = miniMapPoint + new Vector2(.5f, .5f);
                        Vector2 viewPortWorldPoint = Level.MiniMapCamera.ViewportToWorldPoint(viewPortPoint);
                        Vector2 localized = viewPortWorldPoint - Level.GetPosition();

                        //Debug.Log($"Hit: Screen position: {hit.screenPosition}, Mini Map position: {miniMapPoint}, View port position: {viewPortPoint}, Viewport World Point: {viewPortWorldPoint}," +
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
                //Debug.Log($"_clickedShip is not null, running click action");
                if (_leftMouseDoubleClicked)
                {
                    Level.GetState().SelectSquadsByShipType(_clickedShip.ShipType);
                }
                else
                {
                    _clickedShip.Clicked(LeftClick, HasEitherControlKey());
                }
                return true;
            }
            else
            {
                //Debug.Log("_clicked ship is null");
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
            if ((Level.Camera.orthographicSize + difference) < Level.Map.MinZoom)
            {
                difference = Level.Camera.orthographicSize - Level.Map.MinZoom;
            }
            Level.Camera.orthographicSize += difference; // orthographic size decreases, zooming in

            MaintainScrollBoundary();
        }
        private void ZoomOut()
        {
            float difference = Level.ZoomSpeed;
            if ((Level.Camera.orthographicSize + difference) > Level.Map.MaxZoom)
            {
                difference = Level.Camera.orthographicSize - Level.Map.MaxZoom;
            }
            Level.Camera.orthographicSize += difference; // orthographic size increases, zooming out

            MaintainScrollBoundary();
        }
        public void MaintainScrollBoundary()
        {
            //Level.MiniMapCamera.transform.position = new Vector3(0, 0, -10);
            if (Level.HasPlayer && !Level.UnlockCamera)
            {
                MaintainHorizontalScrollBoundary(Level.Camera);
                //MaintainHorizontalScrollBoundary(Level.MiniMapCamera);
                MaintainVerticalScrollBoundary(Level.Camera);
                //MaintainVerticalScrollBoundary(Level.MiniMapCamera);
            }

        }
        private void MaintainHorizontalScrollBoundary(Camera camera)
        {
            // make sure the horizontal scroll isn't out of bounds
            Vector3 position = camera.transform.position;

            float camVertExtent = camera.orthographicSize;
            float camHorzExtent = camera.aspect * camVertExtent;
            Bounds mapBounds = Level.Map.SpriteRenderer.bounds;

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
            Bounds mapBounds = Level.Map.SpriteRenderer.bounds;

           
            float bottomBound = mapBounds.min.y + camVertExtent;
            float topBound = mapBounds.max.y - camVertExtent;

            float camY = Mathf.Clamp(position.y, bottomBound, topBound); 

            //Debug.Log($"mapBoundSize: {mapBounds.size}, mapBoundMax: {mapBounds.max}, camPosition: {position}, camVertExtent: {camVertExtent}, bottomBound: {bottomBound}, topBound: {topBound}, camY: {camY}");

            camera.transform.position = new Vector3(position.x, camY, position.z);

        }
    }
}