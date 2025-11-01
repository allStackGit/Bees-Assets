using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Assets.Scripts.Levels
{
    public class LevelInputManager
    {
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
        public Level Level;
        public Stage Stage;
        public const int RightClick = 1;
        public const int LeftClick = 0;
        public List<Timer> Timers = new List<Timer>();
        public List<Turret> TurretsFiringManually = new List<Turret>();
        public EventSystem EventSystem;

        // these are booleans for user inputs that are held down so that we don't need to fire the action the entire time they're held down

        public bool IsShowingRanges;
        public bool IsFiringManually;

        


        public LevelInputManager(Stage stage, Selector selector)
        {
            Stage = stage;
            Level = stage.PrimaryLevel;
            _mousePosition = Stage.Camera.ScreenToWorldPoint(Input.mousePosition);
            Selector = selector;
            EventSystem = Stage.EventSystem;

            LoadHotKeySettings();
        }

        // ================================
        // Fields for SelectSquadByNumber method
        // ================================
        private int _selectSquad_friendlySquads;
        private Squad _selectSquad_squad;
        private Vector2 _selectSquad_position;

        // ================================
        // Fields for Update method
        // ================================
        private int _update_i;

        public void LoadHotKeySettings()
        {
            // _hotKeys is assumed to be a class-level variable already declared elsewhere.
            _hotKeys = ConfigData.GetUserSettingsData().HotKeys;

            // The following commented code remains as-is (for special combined keys).
            // List<KeyCode> combinedKeys = ConfigData.GetUserSettingsData().FindKey("Show Ranges").Keys.ToList();
            // combinedKeys.AddRange(ConfigData.GetUserSettingsData().FindKey("Manual Fire").Keys);
            // _hotKeys.Add(new HotKey("Show Ranges and Manual Fire", combinedKeys, () =>
            // {
            //     Debug.Log("Double action");
            //     ShowRanges();
            //     ManualFire();
            // }, () =>
            // {
            //     Debug.Log("Double release action");
            //     ShowRanges();
            //     ManualFire();
            // }, false, true));

            _hotKeys.ForEach((hotKey) =>
            {
                switch (hotKey.Name)
                {
                    case "Match Speed":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.MatchSpeed();
                        });
                        break;
                    case "Attack on Sight":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.AttackOnSight();
                        });
                        break;
                    case "Cease Fire":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.CeaseFire();
                        });
                        break;
                    case "Patrol":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Patrol();
                        });
                        break;
                    case "Guard":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Guard();
                        });
                        break;
                    case "Chase":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Chase();
                        });
                        break;
                    case "Hold":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Hold();
                        });
                        break;
                    case "Detonate":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Detonate();
                        });
                        break;
                    case "Charge":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.Charge();
                        });
                        break;
                    case "Drop Beacon":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.DropBeacon();
                        });
                        break;
                    case var _ when ConfigData.ShootingStrategyNames.Contains(hotKey.Name):
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.ActionBox.SetShootingStrategy(hotKey.Name);
                        });
                        break;
                    case var _ when hotKey.Name.StartsWith("Select Squad #"):
                        // Instead of declaring a local int here, we use a class-level field.
                        //Debug.Log($"Hot key: {hotKey.Name}, {hotKey.Name.Substring(hotKey.Name.Length - 1)}");
                        hotKey.SetAction(() =>
                        {
                            SelectSquadByNumber(int.Parse(hotKey.Name.Substring(hotKey.Name.Length - 1)));
                        });
                        break;
                    case "Open Menu":
                        hotKey.SetAction(() =>
                        {
                            Stage.Menus.OpenMenu();
                            hotKey.ManuallySetInputRelease(true);
                        });
                        break;
                    case "Show Ranges":
                        hotKey.SetAction(() =>
                        {
                            if (!IsShowingRanges)
                            {
                                ShowRanges();
                            }
                        });
                        hotKey.SetReleaseAction(() =>
                        {
                            if (IsShowingRanges)
                            {
                                ShowRanges();
                            }
                        });
                        break;
                    case "Manual Fire":
                        hotKey.SetAction(() =>
                        {
                            if (!IsFiringManually)
                            {
                                ManualFire();
                            }
                        });
                        hotKey.SetReleaseAction(() =>
                        {
                            if (IsFiringManually)
                            {
                                ManualFire();
                            }
                        });
                        break;
                    case "Show Ranges + Manual Fire":
                        hotKey.SetAction(() =>
                        {
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
                            Stage.Menus.ToggleMiniMapDisplay();
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
            });
        }

        public void ShowRanges()
        {
            if (!IsShowingRanges)
            {
                Level.State.GetSelectedSquads().ForEach(s =>
                {
                    if (!s.IsShowingRanges)
                    {
                        s.ShowSquadRanges();
                    }
                });
                IsShowingRanges = true;
            }
            else
            {
                Level.State.GetSelectedSquads().ForEach(s =>
                {
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
                Level.State.GetSelectedSquads().ForEach(squad =>
                {
                    squad.GetShips().ForEach((ship) =>
                    {
                        ship.Turrets.ForEach((turret) =>
                        {
                            turret.IsFiringManually = true;
                            TurretsFiringManually.Add(turret);
                        });
                        if (ship.ShipType == ConfigData.ShipTypes.Flagship)
                        {
                            ship.StopMoving("Flagship is manually firing");
                        }
                    });
                });
                IsFiringManually = true;
                Cursor.SetCursor(Stage.ManualFireCursor, Stage.CursorSpot, CursorMode.Auto);
            }
            else
            {
                TurretsFiringManually.ForEach((turret) =>
                {
                    turret.IsFiringManually = false;
                });
                IsFiringManually = false;
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
        }

        public void SelectSquadByNumber(int squadNumber)
        {
            //Debug.Log($"Squad #{squadNumber}");
            if (squadNumber == 0)
            {
                squadNumber = 10;
            }

            // Instead of a locally declared variable, use class-level fields.
            //Debug.Log($"Config UserSide: {ConfigData.Configuration.UserSide}, squads: {Level.State.OriginalSquadCounts[ConfigData.Configuration.UserSide - 1]}");
            _selectSquad_friendlySquads = Level.State.OriginalSquadCounts[ConfigData.Configuration.UserSide - 1];
            squadNumber %= _selectSquad_friendlySquads;
            if (squadNumber == 0)
            {
                squadNumber = _selectSquad_friendlySquads;
            }
            //Debug.Log($"Modded Squad #{squadNumber}");

            _selectSquad_squad = Level.State.GetSquadByNumber(ConfigData.Configuration.UserSide, squadNumber);
            Level.State.SelectSquad(_selectSquad_squad);

            if (_selectSquad_squad != null && _selectSquad_squad.IsSelected) //Center the camera on the selected squad. The squad can be null if there are no available squads to select
            {
                _selectSquad_position = _selectSquad_squad.GetPosition();
                Stage.Camera.transform.position = new Vector3(_selectSquad_position.x, _selectSquad_position.y, -10) + Level.Get3DPosition();
                //ToggleZoom();
                MaintainScrollBoundary();
            }
        }

        public void Update()
        {
            CheckInputs();
            CheckActions();
            ResetInputs();
            // Declare the loop variable as a class-level field (_update_i) instead of a local variable.
            for (_update_i = 0; _update_i < Timers.Count; _update_i++)
            {
                if (Timers[_update_i].Update())
                {
                    Timers.RemoveAt(_update_i);
                }
            }
            // Debug.Log($"width: {Screen.width}, height: {Screen.height}, mousePosition: {_mousePosition}");
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
        // ===========================================================
        // Fields used in CheckInputs()
        // ===========================================================
        private Vector2 _checkInputs_mouse; // formerly: Vector2 mouse

        // ===========================================================
        // Fields used in HasMiningCommandInput()
        // ===========================================================
        private RaycastHit2D[] _hasMiningCommandInput_hits;
        private int _hasMiningCommandInput_i;
        private RaycastHit2D _hasMiningCommandInput_hit;

        // ===========================================================
        // Fields used in CheckActions()
        // ===========================================================
        private float _checkActions_mouseScrollSpeed; // formerly: float mouseScrollSpeed

        // -----------------------------------------------------------
        // (Other class-level fields already declared elsewhere, e.g.:
        // _mousePosition, _previousMousePosition, _leftMouseButtonDown, etc.)
        // -----------------------------------------------------------

        private void CheckInputs()
        {
            // Save previous mouse position
            _previousMousePosition = _mousePosition;

            // Get the current mouse position from the input
            _checkInputs_mouse = Input.mousePosition;
            _mousePosition = Stage.Camera.ScreenToWorldPoint(_checkInputs_mouse);

            // --- (Commented out debug code for testing key presses) ---

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
                if (EventSystem.IsPointerOverGameObject())
                {
                    CheckForMiniMapNavigation(LeftClick);
                    return;
                }
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
            }
            else if (Input.GetMouseButtonDown(RightClick)) // right mouse button down
            {
                if (EventSystem.IsPointerOverGameObject())
                {
                    CheckForMiniMapNavigation(RightClick);
                    return;
                }
                _rightMouseButtonUp = false;
                _isRightMouseDownPrior = true;
                Timers.Add(new Timer(.25f, SetRightMouseDownLongEnoughForDragging));
            }
            else if (Input.GetMouseButtonUp(RightClick)) // right mouse button up
            {
                if (EventSystem.IsPointerOverGameObject()) return;
                _rightMouseButtonUp = true;
                _isRightMouseDownPrior = false;
                _isRightMouseDragging = false;
            }

            if (Input.GetAxis("Mouse ScrollWheel") > 0)
            {
                _scrollPositive = true;
            }
            else if (Input.GetAxis("Mouse ScrollWheel") < 0)
            {
                _scrollNegative = true;
            }

            if (_checkInputs_mouse.x < Utilities.WorldUnitsToScreenPixels(Stage.MouseScrollDistanceFromEdge, Stage.Camera).x)
            {
                _mouseAtLeftEdge = true;
            }
            else if (_checkInputs_mouse.x >= ConfigData.ScreenWidth - Utilities.WorldUnitsToScreenPixels(Stage.MouseScrollDistanceFromEdge, Stage.Camera).x)
            {
                _mouseAtRightEdge = true;
            }

            if (_checkInputs_mouse.y < Utilities.WorldUnitsToScreenPixels(Stage.MouseScrollDistanceFromEdge, Stage.Camera).y)
            {
                _mouseAtBottomEdge = true;
            }
            else if (_checkInputs_mouse.y >= ConfigData.ScreenHeight - Utilities.WorldUnitsToScreenPixels(Stage.MouseScrollDistanceFromEdge, Stage.Camera).y)
            {
                _mouseAtTopEdge = true;
            }

            if (Input.GetMouseButton(LeftClick))
            {
                if (EventSystem.IsPointerOverGameObject()) return;
                if (Mathf.Abs(Input.GetAxis("Mouse X")) > .5f || Mathf.Abs(Input.GetAxis("Mouse Y")) > .5f)
                {
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
            // No locally declared variables.
            if (Stage.Camera.orthographicSize == Level.Map.MaxZoom)
            {
                Stage.Camera.orthographicSize = Stage.DefaultZoom;
            }
            else
            {
                Stage.Camera.orthographicSize = Level.Map.MaxZoom;
            }
        }

        private void SetRightMouseDownLongEnoughForDragging()
        {
            // No locally declared variables.
            if (_isRightMouseDownPrior)
            {
                _isRightMouseDragging = true;
            }
        }

        private bool HasDragMoveSquadsInput()
        {
            // No new local variables are declared here.
            if (!_isDragMovementBlockedByTimer &&
                !EventSystem.IsPointerOverGameObject() &&
                _isRightMouseDragging &&
                Vector2.Distance(_previousDragMousePosition, _mousePosition) > 5)
            {
                _isDragMovementBlockedByTimer = true;
                _previousDragMousePosition = _mousePosition;
                Timers.Add(new Timer(.25f, UnblockDragMovement));
                return Input.GetMouseButton(RightClick);
            }
            return false;
        }

        private void UnblockDragMovement()
        {
            // No locally declared variables.
            _isDragMovementBlockedByTimer = false;
        }

        private bool HasMoveSquadsInput()
        {
            // No locally declared variables.
            if (EventSystem.IsPointerOverGameObject())
            {
                return false;
            }
            return Input.GetMouseButtonUp(RightClick);
        }

        private bool HasSelectingGuardShipInput()
        {
            // No locally declared variables.
            return _selectingGuardTarget && Input.GetMouseButtonUp(RightClick) && _clickedShip != null && _clickedShip.IsUserControlled;
        }

        private bool HasAttackingShipInput()
        {
            // No locally declared variables.
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
                // Perform a raycast from the current mouse position.
                _hasMiningCommandInput_hits = Physics2D.RaycastAll(Stage.Camera.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                for (_hasMiningCommandInput_i = 0; _hasMiningCommandInput_i < _hasMiningCommandInput_hits.Length; _hasMiningCommandInput_i++)
                {
                    _hasMiningCommandInput_hit = _hasMiningCommandInput_hits[_hasMiningCommandInput_i];
                    if (_hasMiningCommandInput_hit.collider != null && _hasMiningCommandInput_hit.collider.CompareTag("Mining Asteroid"))
                    {
                        _clickedMiningAsteroid = _hasMiningCommandInput_hit.collider.gameObject.GetComponent<MiningAsteroid>();
                        return true;
                    }
                }
            }
            return false;
        }

        private bool HasFullRetreatCommandInput()
        {
            // No locally declared variables.
            return Input.GetMouseButtonUp(RightClick) && _clickedShip != null && _clickedShip.IsWarpGate && ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide;
        }
        private bool HasHealCommandInput()
        {
            // No locally declared variables.
            return Input.GetMouseButtonUp(RightClick) && _clickedShip != null && _clickedShip.IsBeehive && ConfigData.Configuration.UserSide == ConfigData.Configuration.BeeSide;
        }
        private bool HasEitherControlKey()
        {
            // No locally declared variables.
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        }

        public bool HasPauseInput()
        {
            // No locally declared variables.
            return false; // [alert] turned off for wide release beta
            //return Input.GetKey(KeyCode.P);
        }

        /// <summary>
        /// The new logic is as follows:
        /// 1. As many inputs will be able to be customized in settings, it's important to abstract the input logic for an action away from the specific keys
        /// 2. Check each action individually to see if it's been triggered
        /// </summary>
        private void CheckActions()
        {
            // No local variables declared except for the following float:
            _checkActions_mouseScrollSpeed = Stage.ScrollSpeed * 10 * Time.deltaTime;

            foreach (HotKey hotKey in _hotKeys)
            {
                if (hotKey.CheckInput())
                {
                    // Input registered from hotKey.
                }
            }
            if (HasPauseInput())
            {
                Debug.Break();
                //if (!Level.State.IsPaused && Time.realtimeSinceStartup - Level.TimePaused > 1)
                //{
                //    Level.IsPausedByTester = true;
                //    Level.TimePaused = Time.realtimeSinceStartup;
                //    Level.Pause();
                //    Debug.Break();
                //    return;
                //}
            }

            if (!Level.State.IsPaused)
            {
                CheckClickCollision();

                if (HasDragMoveSquadsInput())
                {
                    MoveSquads(Stage.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (HasSelectingGuardShipInput())
                {
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
                else if (HasHealCommandInput())
                {
                    SetSquadsToHeal(_clickedShip.Squad.GetShips().Where((s) => s.ShipType == ConfigData.ShipTypes.Beehive).Select((b) => (Beehive)b).ToList());
                }
                else if (HasMiningCommandInput())
                {
                    SetSquadsToMine(_clickedMiningAsteroid);
                }
                else if (HasMoveSquadsInput())
                {
                    MoveSquads(Stage.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
                else if (_leftMouseButtonUp)
                {
                    if (_isLeftMouseDragging && !_selectingPatrolArea)
                    {
                        Selector.SelectShipsInBox();
                    }
                    else if (!LeftClickAction())
                    {
                        Selector.SelectShipsInBox();
                    }
                    _isLeftMouseDragging = false;
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

                if (Stage.UseMouseScrolling)
                {
                    if (_mouseAtLeftEdge)
                    {
                        ScrollLeft(_checkActions_mouseScrollSpeed);
                    }
                    else if (_mouseAtRightEdge)
                    {
                        ScrollRight(_checkActions_mouseScrollSpeed);
                    }

                    if (_mouseAtTopEdge)
                    {
                        ScrollUp(_checkActions_mouseScrollSpeed);
                    }
                    else if (_mouseAtBottomEdge)
                    {
                        ScrollDown(_checkActions_mouseScrollSpeed);
                    }
                }
            }
        }

        public Vector2 GetMousePosition()
        {
            // No locally declared variables.
            return _mousePosition;
        }

        public void SetPatrolAreaActive()
        {
            // No locally declared variables.
            Cursor.SetCursor(Stage.PatrolCursor, Vector2.zero, CursorMode.Auto);
            _selectingPatrolArea = true;
        }

        public void SetSelectGuardTargetActive()
        {
            // No locally declared variables.
            Cursor.SetCursor(Stage.GuardCursor, Vector2.zero, CursorMode.Auto);
            _selectingGuardTarget = true;
        }

        private void SetSelectingGuard(Ship ship)
        {
            // No locally declared variables.
            Level.State.GetSelectedSquads().ForEach((squad) =>
            {
                squad.UserGuard(ship.Squad); // Make all selected ships guard this squad.
            });
            _selectingGuardTarget = false;
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);

        }
        // ===========================================================
        // Fields for CheckForSelectingSquad()
        // ===========================================================
        private List<Ship> _checkForSelectingSquad_ships;
        private Squad _checkForSelectingSquad_potentialSquad;
        private Vector2 _checkForSelectingSquad_levelPosition;
        private int _checkForSelectingSquad_i;
        private Ship _checkForSelectingSquad_currentShip;

        // ===========================================================
        // Fields for CheckForAttackAISquad()
        // ===========================================================
        private List<Ship> _checkForAttackAISquad_ships;
        private Squad _checkForAttackAISquad_potentialSquad;
        private Vector2 _checkForAttackAISquad_levelPosition;
        private int _checkForAttackAISquad_i;
        private Ship _checkForAttackAISquad_currentShip;

        // ===========================================================
        // Fields for MoveSquads()
        // ===========================================================
        private Vector2 _moveSquads_localized;
        private List<Squad> _moveSquads_selectedSquads;
        private int _moveSquads_i;

        // ===========================================================
        // Fields for CheckForSelectingPatrolArea()
        // ===========================================================
        private Vector2 _checkForSelectingPatrolArea_startingPosition;
        private Vector2 _checkForSelectingPatrolArea_endingPosition;

        // ===========================================================
        // Fields for CheckClickCollision()
        // ===========================================================
        private RaycastHit2D[] _checkClickCollision_hits;
        private int _checkClickCollision_i;
        private RaycastHit2D _checkClickCollision_hit;
        private Ship _checkClickCollision_ship;

        // ===========================================================
        // Fields for CheckForMiniMapNavigation()
        // ===========================================================
        private PointerEventData _checkForMiniMapNavigation_eventData;
        private List<RaycastResult> _minimapNavigationResults;
        private int _checkForMiniMapNavigation_j;
        private RaycastResult _checkForMiniMapNavigation_hit;
        private Vector2 _minimapPoint;
        private Vector2 _viewportPoint;
        private Vector2 _viewportWorldPoint;
        private Vector2 _localizedPoint;
        private Vector2 _checkForMiniMapNavigation_half = new Vector2(.5f, .5f);

        // ===========================================================
        // Method: CheckForSelectingSquad()
        // ===========================================================
        private bool CheckForSelectingSquad()
        {
            _checkForSelectingSquad_ships = Level.State.GetShips(ConfigData.Configuration.UserSide);
            _checkForSelectingSquad_potentialSquad = null;
            _checkForSelectingSquad_levelPosition = _mousePosition - Level.GetPosition();

            for (_checkForSelectingSquad_i = 0; _checkForSelectingSquad_i < _checkForSelectingSquad_ships.Count; _checkForSelectingSquad_i++)
            {
                _checkForSelectingSquad_currentShip = _checkForSelectingSquad_ships[_checkForSelectingSquad_i];
                if (_checkForSelectingSquad_currentShip.DistanceToPoint(_checkForSelectingSquad_levelPosition) <= 5)
                {
                    _checkForSelectingSquad_potentialSquad = _checkForSelectingSquad_currentShip.Squad;
                    _clickedShip = _checkForSelectingSquad_currentShip;
                }
            }

            if (_checkForSelectingSquad_potentialSquad != null)
            {
                if (HasEitherControlKey())
                {
                    Level.State.AddSelectedSquad(_checkForSelectingSquad_potentialSquad);
                }
                else if (_leftMouseDoubleClicked)
                {
                    Level.State.SelectSquadsByShipType(_clickedShip.ShipType);
                }
                else
                {
                    Level.State.SelectSquad(_checkForSelectingSquad_potentialSquad);
                }
                return true;
            }
            return false;
        }

        // ===========================================================
        // Method: CheckForAttackAISquad()
        // ===========================================================
        private bool CheckForAttackAISquad()
        {
            _checkForAttackAISquad_ships = Level.State.GetShips(ConfigData.Configuration.AISide);
            _checkForAttackAISquad_potentialSquad = null;
            _checkForAttackAISquad_levelPosition = _mousePosition - Level.GetPosition();

            for (_checkForAttackAISquad_i = 0; _checkForAttackAISquad_i < _checkForAttackAISquad_ships.Count; _checkForAttackAISquad_i++)
            {
                _checkForAttackAISquad_currentShip = _checkForAttackAISquad_ships[_checkForAttackAISquad_i];
                if (_checkForAttackAISquad_currentShip.DistanceToPoint(_checkForAttackAISquad_levelPosition) <= 10)
                {
                    _checkForAttackAISquad_potentialSquad = _checkForAttackAISquad_currentShip.Squad;
                    _clickedShip = _checkForAttackAISquad_currentShip;
                }
            }
            if (_checkForAttackAISquad_potentialSquad != null)
            {
                _checkForAttackAISquad_potentialSquad.GetShips().First().Clicked(RightClick);
                return true;
            }
            return false;
        }

        // ===========================================================
        // Method: MoveSquads()
        // ===========================================================
        private void MoveSquads(Vector2 targetPosition)
        {
            _moveSquads_selectedSquads = Level.State.GetSelectedSquads().Where((s) => !s.IsLockedOn).ToList();
            for (_moveSquads_i = 0; _moveSquads_i < _moveSquads_selectedSquads.Count; _moveSquads_i++)
            {
                _moveSquads_localized = targetPosition - Level.GetPosition();
                _moveSquads_selectedSquads[_moveSquads_i].FinalizeUserCommand();
                _moveSquads_selectedSquads[_moveSquads_i].Move(_moveSquads_localized);
            }
        }

        // ===========================================================
        // Method: SetSquadsToMine()
        // ===========================================================
        private void SetSquadsToMine(MiningAsteroid asteroid)
        {
            // No local variables need extraction here.
            Level.State.GetSelectedSquads().ForEach((squad) =>
            {   if (squad.GetShips().Any((s) => s.ShipType == ConfigData.ShipTypes.Factory))
                {
                    squad.UserMining(asteroid);
                }
                else
                {
                    squad.Move(Stage.Camera.ScreenToWorldPoint(Input.mousePosition));
                }
            });
        }

        // ===========================================================
        // Method: SetSquadsToFullRetreat()
        // ===========================================================
        private void SetSquadsToFullRetreat(WarpGate warpGate)
        {
            // No local variables need extraction here.
            Level.State.GetSelectedSquads().ForEach((squad) =>
            {
                if (squad.GetShips().Any((s) => s.ShipType != ConfigData.ShipTypes.WarpGate))
                {
                    squad.UserFullRetreat(warpGate);
                }
            });
        }

        private void SetSquadsToHeal(List<Beehive> beehives)
        {
            // No local variables need extraction here.
            Level.State.GetSelectedSquads().ForEach((squad) =>
            {
                squad.UserHeal(beehives);
            });
        }

        // ===========================================================
        // Method: CheckForSelectingPatrolArea()
        // ===========================================================
        private bool CheckForSelectingPatrolArea()
        {
            if (_selectingPatrolArea)
            {
                Level.State.GetSelectedSquads().ForEach((squad) =>
                {
                    _checkForSelectingPatrolArea_startingPosition = _mouseDownPosition - Level.GetPosition();
                    _checkForSelectingPatrolArea_endingPosition = _mousePosition - Level.GetPosition();
                    squad.UserPatrol(_checkForSelectingPatrolArea_startingPosition, _checkForSelectingPatrolArea_endingPosition);
                });
                _selectingPatrolArea = false;
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                return true;
            }
            return false;
        }

        // ===========================================================
        // Method: CheckClickCollision()
        // ===========================================================
        private void CheckClickCollision()
        {
            if (_leftMouseButtonUp || _rightMouseButtonUp)
            {
                _checkClickCollision_hits = Physics2D.RaycastAll(Stage.Camera.ScreenToWorldPoint(Input.mousePosition), Vector2.zero);
                for (_checkClickCollision_i = 0; _checkClickCollision_i < _checkClickCollision_hits.Length; _checkClickCollision_i++)
                {
                    _checkClickCollision_hit = _checkClickCollision_hits[_checkClickCollision_i];
                    if (_checkClickCollision_hit.collider != null)
                    {
                        if (_checkClickCollision_hit.collider.CompareTag("Ship"))
                        {
                            _checkClickCollision_ship = _checkClickCollision_hit.collider.gameObject.GetComponent<Ship>();
                            _clickedShip = _checkClickCollision_ship;
                        }
                        else if (_checkClickCollision_hit.collider.CompareTag("Beehive Heal Collider"))
                        {
                            _checkClickCollision_ship = _checkClickCollision_hit.collider.transform.parent.gameObject.GetComponent<Ship>();
                            _clickedShip = _checkClickCollision_ship;
                        }

                    }
                }
            }
        }

        // ===========================================================
        // Method: CheckForMiniMapNavigation()
        // ===========================================================
        private void CheckForMiniMapNavigation(int mouseButton)
        {
            if (!Stage.Menus.HoveringOverMiniMapButton)
            {
                //Debug.Log("Mouse is over the mini map camera collider.");
                _checkForMiniMapNavigation_eventData = new PointerEventData(EventSystem.current);
                _checkForMiniMapNavigation_eventData.position = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

                _minimapNavigationResults = new List<RaycastResult>();
                EventSystem.RaycastAll(_checkForMiniMapNavigation_eventData, _minimapNavigationResults);

                //Debug.Log($"_minimapNavigationResults.Count: {_minimapNavigationResults.Count}");

                for (_checkForMiniMapNavigation_j = 0; _checkForMiniMapNavigation_j < _minimapNavigationResults.Count; _checkForMiniMapNavigation_j++)
                {
                    _checkForMiniMapNavigation_hit = _minimapNavigationResults[_checkForMiniMapNavigation_j];
                    if (_checkForMiniMapNavigation_hit.gameObject.name == "Camera Collider")
                    {
                        //Debug.Log("Mouse hit the mini map camera collider.");
                        _minimapPoint = _checkForMiniMapNavigation_hit.gameObject.transform.InverseTransformPoint(_checkForMiniMapNavigation_hit.screenPosition);
                        _viewportPoint = _minimapPoint + _checkForMiniMapNavigation_half;
                        _viewportWorldPoint = Stage.MiniMapCamera.ViewportToWorldPoint(_viewportPoint);
                        _localizedPoint = _viewportWorldPoint - Level.GetPosition();

                        if (mouseButton == RightClick)
                        {
                            //Debug.Log($"Moving squads, right click");
                            MoveSquads(_viewportWorldPoint);
                        }
                        else
                        {
                            //Debug.Log($"Moving camera, left click");
                            Stage.Camera.transform.localPosition = new Vector3(_localizedPoint.x, _localizedPoint.y, -10);
                            MaintainScrollBoundary();
                        }
                    }
                }
            }
            //else
            //{
            //    Debug.Log("Mouse is over the mini map close button.");
            //}
        }

        // ===========================================================
        // Method: LeftClickAction()
        // ===========================================================
        private bool LeftClickAction()
        {
            if (_clickedShip != null)
            {
                if (_leftMouseDoubleClicked)
                {
                    Level.State.SelectSquadsByShipType(_clickedShip.ShipType);
                }
                else
                {
                    _clickedShip.Clicked(LeftClick, HasEitherControlKey());
                }
                return true;
            }
            else
            {
                if (!CheckForSelectingSquad())
                {
                    if (!CheckForSelectingPatrolArea())
                    {
                        return false;
                    }
                }
                return true;
            }
        }




        // scrolling methods
        // ===========================================================
        // Fields for ScrollRight()
        // ===========================================================
        private Vector3 _scrollRight_position;

        // ===========================================================
        // Fields for ScrollLeft()
        // ===========================================================
        private Vector3 _scrollLeft_position;

        // ===========================================================
        // Fields for ScrollUp()
        // ===========================================================
        private Vector3 _scrollUp_position;

        // ===========================================================
        // Fields for ScrollDown()
        // ===========================================================
        private Vector3 _scrollDown_position;

        // ===========================================================
        // Fields for ZoomIn()
        // ===========================================================
        private float _zoomIn_difference;

        // ===========================================================
        // Fields for ZoomOut()
        // ===========================================================
        private float _zoomOut_difference;

        // ===========================================================
        // Fields for MaintainHorizontalScrollBoundary()
        // ===========================================================
        private Vector3 _maintainHorizontal_position;
        private float _maintainHorizontal_camVertExtent;
        private float _maintainHorizontal_camHorzExtent;
        private Bounds _maintainHorizontal_mapBounds;
        private float _maintainHorizontal_leftBound;
        private float _maintainHorizontal_rightBound;
        private float _maintainHorizontal_camX;

        // ===========================================================
        // Fields for MaintainVerticalScrollBoundary()
        // ===========================================================
        private Vector3 _maintainVertical_position;
        private float _maintainVertical_camVertExtent;
        private Bounds _maintainVertical_mapBounds;
        private float _maintainVertical_bottomBound;
        private float _maintainVertical_topBound;
        private float _maintainVertical_camY;

        // -----------------------------------------------------------
        // ScrollRight(): Moves the camera to the right.
        // -----------------------------------------------------------
        private void ScrollRight(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Stage.ScrollSpeed;
            }
            _scrollRight_position = Stage.Camera.transform.position;
            Stage.Camera.transform.position = new Vector3(_scrollRight_position.x + scrollSpeed, _scrollRight_position.y, -10);
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // ScrollLeft(): Moves the camera to the left.
        // -----------------------------------------------------------
        private void ScrollLeft(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Stage.ScrollSpeed;
            }
            _scrollLeft_position = Stage.Camera.transform.position;
            Stage.Camera.transform.position = new Vector3(_scrollLeft_position.x - scrollSpeed, _scrollLeft_position.y, -10);
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // ScrollUp(): Moves the camera upward.
        // -----------------------------------------------------------
        private void ScrollUp(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Stage.ScrollSpeed;
            }
            _scrollUp_position = Stage.Camera.transform.position;
            Stage.Camera.transform.position = new Vector3(_scrollUp_position.x, _scrollUp_position.y + scrollSpeed, -10);
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // ScrollDown(): Moves the camera downward.
        // -----------------------------------------------------------
        private void ScrollDown(float scrollSpeed = 0)
        {
            if (scrollSpeed == 0)
            {
                scrollSpeed = Stage.ScrollSpeed;
            }
            _scrollDown_position = Stage.Camera.transform.position;
            Stage.Camera.transform.position = new Vector3(_scrollDown_position.x, _scrollDown_position.y - scrollSpeed, -10);
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // ZoomIn(): Zooms the camera in (decreases orthographic size).
        // -----------------------------------------------------------
        private void ZoomIn()
        {
            _zoomIn_difference = -1 * Stage.ZoomSpeed;
            if ((Stage.Camera.orthographicSize + _zoomIn_difference) < Level.Map.MinZoom)
            {
                _zoomIn_difference = Stage.Camera.orthographicSize - Level.Map.MinZoom;
            }
            Stage.Camera.orthographicSize += _zoomIn_difference;
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // ZoomOut(): Zooms the camera out (increases orthographic size).
        // -----------------------------------------------------------
        private void ZoomOut()
        {
            _zoomOut_difference = Stage.ZoomSpeed;
            if ((Stage.Camera.orthographicSize + _zoomOut_difference) > Level.Map.MaxZoom)
            {
                _zoomOut_difference = Stage.Camera.orthographicSize - Level.Map.MaxZoom;
            }
            Stage.Camera.orthographicSize += _zoomOut_difference;
            MaintainScrollBoundary();
        }

        // -----------------------------------------------------------
        // MaintainScrollBoundary(): Ensures the camera remains within map bounds.
        // -----------------------------------------------------------
        public void MaintainScrollBoundary()
        {
            // No local variables declared here.
            if (!Stage.UnlockCamera)
            {
                MaintainHorizontalScrollBoundary(Stage.Camera);
                MaintainVerticalScrollBoundary(Stage.Camera);
            }
        }

        // -----------------------------------------------------------
        // MaintainHorizontalScrollBoundary(): Clamps the camera's horizontal position.
        // -----------------------------------------------------------
        private void MaintainHorizontalScrollBoundary(Camera camera)
        {
            _maintainHorizontal_position = camera.transform.position;
            _maintainHorizontal_camVertExtent = camera.orthographicSize;
            _maintainHorizontal_camHorzExtent = camera.aspect * _maintainHorizontal_camVertExtent;
            //Debug.Log($"{Level}, {Level?.Map}, {Level?.Map?.SpriteRenderer}, {Level?.Map?.SpriteRenderer?.bounds}");
            _maintainHorizontal_mapBounds = Level.Map.SpriteRenderer.bounds;

            _maintainHorizontal_leftBound = _maintainHorizontal_mapBounds.min.x + _maintainHorizontal_camHorzExtent;
            _maintainHorizontal_rightBound = _maintainHorizontal_mapBounds.max.x - _maintainHorizontal_camHorzExtent;

            _maintainHorizontal_camX = Mathf.Clamp(_maintainHorizontal_position.x, _maintainHorizontal_leftBound, _maintainHorizontal_rightBound);
            camera.transform.position = new Vector3(_maintainHorizontal_camX, _maintainHorizontal_position.y, _maintainHorizontal_position.z);
        }

        // -----------------------------------------------------------
        // MaintainVerticalScrollBoundary(): Clamps the camera's vertical position.
        // -----------------------------------------------------------
        private void MaintainVerticalScrollBoundary(Camera camera)
        {
            _maintainVertical_position = camera.transform.position;
            _maintainVertical_camVertExtent = camera.orthographicSize;
            _maintainVertical_mapBounds = Level.Map.SpriteRenderer.bounds;

            _maintainVertical_bottomBound = _maintainVertical_mapBounds.min.y + _maintainVertical_camVertExtent;
            _maintainVertical_topBound = _maintainVertical_mapBounds.max.y - _maintainVertical_camVertExtent;

            _maintainVertical_camY = Mathf.Clamp(_maintainVertical_position.y, _maintainVertical_bottomBound, _maintainVertical_topBound);
            camera.transform.position = new Vector3(_maintainVertical_position.x, _maintainVertical_camY, _maintainVertical_position.z);
        }
    }
}