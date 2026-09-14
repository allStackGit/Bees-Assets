using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps the opening Pluto encounter readable without changing the authored mission flow.
    /// The player still explores with the Scout; once the first Bee is found, this guard briefly
    /// directs the camera to the Bee and progressively introduces the squad command controls.
    /// </summary>
    [DefaultExecutionOrder(1300)]
    public sealed class PlutoIntroFeedbackGuard : MonoBehaviour
    {
        private const int PlutoOneMissionId = 0;
        private const float RevealDuration = 0.7f;
        private const float RevealZoomFraction = 0.08f;
        private const float MaximumRevealZoom = 4f;
        private const float CommandPulseAmount = 0.07f;
        private const float CommandPulseSpeed = 6f;

        private readonly Dictionary<GameObject, bool> _hiddenCommandStates = new Dictionary<GameObject, bool>();

        private Stage _stage;
        private Vector3 _lastCameraPosition;
        private bool _hasLastCameraPosition;
        private bool _revealActive;
        private bool _revealComplete;
        private float _revealStartedAt;
        private Vector3 _revealStartPosition;
        private float _revealStartZoom;
        private bool _restorePlayerControl;
        private Honeybee _revealHoneybee;

        private bool _commandPresentationApplied;
        private Vector3 _attackOnSightBaseScale;
        private GameObject _attackOnSightButton;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (GameObject.Find("Pluto Intro Feedback Guard") != null)
            {
                return;
            }

            GameObject host = new GameObject("Pluto Intro Feedback Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<PlutoIntroFeedbackGuard>();
        }

        private void LateUpdate()
        {
            if (!ResolveStage())
            {
                return;
            }

            if (!IsPlutoOne())
            {
                ResetPresentation();
                return;
            }

            UpdateFirstBeeReveal();
            UpdateCommandTutorial();
        }

        private bool ResolveStage()
        {
            if (_stage != null && _stage.PrimaryLevel != null && _stage.Camera != null)
            {
                return true;
            }

            Stage resolved = FindObjectOfType<Stage>();
            if (resolved == null || resolved.PrimaryLevel == null || resolved.Camera == null)
            {
                return false;
            }

            if (resolved != _stage)
            {
                ResetPresentation();
                _stage = resolved;
                _lastCameraPosition = _stage.Camera.transform.position;
                _hasLastCameraPosition = true;
            }
            return true;
        }

        private bool IsPlutoOne()
        {
            return ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                   ConfigData.UserProgressData != null &&
                   ConfigData.Configuration != null &&
                   ConfigData.UserProgressData.GetCurrentLevel(
                       ConfigData.Configuration.UserSide,
                       ConfigData.GameModes.Campaign) == PlutoOneMissionId;
        }

        private void UpdateFirstBeeReveal()
        {
            if (_revealActive)
            {
                ContinueFirstBeeReveal();
                return;
            }

            if (!_revealComplete && IsFirstBeeRevealStarting())
            {
                BeginFirstBeeReveal();
                ContinueFirstBeeReveal();
                return;
            }

            if (_stage.Camera != null)
            {
                _lastCameraPosition = _stage.Camera.transform.position;
                _hasLastCameraPosition = true;
            }
        }

        private bool IsFirstBeeRevealStarting()
        {
            Scout scout = _stage.CameraShip as Scout;
            if (scout == null || scout.Squad == null || scout.Squad.CanAcceptUserInput ||
                !_stage.IsFollowingShip || _stage.PrimaryLevel.State == null)
            {
                return false;
            }

            _revealHoneybee = _stage.PrimaryLevel.State.GetBeeShips()
                .OfType<Honeybee>()
                .FirstOrDefault(bee => bee != null && !bee.IsDead);
            return _revealHoneybee != null;
        }

        private void BeginFirstBeeReveal()
        {
            _revealActive = true;
            _revealStartedAt = Time.unscaledTime;
            _revealStartPosition = _hasLastCameraPosition
                ? _lastCameraPosition
                : _stage.Camera.transform.position;
            _revealStartZoom = _stage.Camera.orthographicSize;
            _restorePlayerControl = _stage.IsPlayerControlling;

            // The mission already locks the Scout itself at discovery. Only suspend the remaining
            // camera/input layer for the short reveal, and do not snap back to following the Scout.
            _stage.IsPlayerControlling = false;
            _stage.IsFollowingShip = false;
            _stage.IsCameraMovingToTarget = false;
            _stage.Camera.transform.position = _revealStartPosition;
        }

        private void ContinueFirstBeeReveal()
        {
            if (_stage == null || _stage.Camera == null || _revealHoneybee == null)
            {
                FinishFirstBeeReveal();
                return;
            }

            float progress = Mathf.Clamp01((Time.unscaledTime - _revealStartedAt) / RevealDuration);
            float eased = EvaluateRevealProgress(progress);
            Vector3 target = _revealHoneybee.GetPosition();
            target.z = _revealStartPosition.z;
            _stage.Camera.transform.position = Vector3.LerpUnclamped(_revealStartPosition, target, eased);

            float zoomAmount = Mathf.Min(MaximumRevealZoom, Mathf.Max(0.5f, _revealStartZoom * RevealZoomFraction));
            _stage.Camera.orthographicSize = _revealStartZoom - EvaluateRevealZoom(progress, zoomAmount);

            if (progress >= 1f)
            {
                FinishFirstBeeReveal();
            }
        }

        private void FinishFirstBeeReveal()
        {
            if (_stage != null)
            {
                if (_stage.Camera != null)
                {
                    if (_revealHoneybee != null)
                    {
                        Vector3 target = _revealHoneybee.GetPosition();
                        target.z = _stage.Camera.transform.position.z;
                        _stage.Camera.transform.position = target;
                    }
                    if (_revealStartZoom > 0f)
                    {
                        _stage.Camera.orthographicSize = _revealStartZoom;
                    }
                }
                _stage.IsPlayerControlling = _restorePlayerControl;
            }

            _revealActive = false;
            _revealComplete = true;
            _revealHoneybee = null;
        }

        internal static float EvaluateRevealProgress(float progress)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress));
        }

        internal static float EvaluateRevealZoom(float progress, float amount)
        {
            return Mathf.Sin(Mathf.PI * Mathf.Clamp01(progress)) * Mathf.Max(0f, amount);
        }

        private void UpdateCommandTutorial()
        {
            if (!ConfigData.UserProgressData.ShowToolTips || _stage.Menus == null ||
                _stage.Menus.ActionBox == null || _stage.PrimaryLevel.CurrentLevelOptions == null ||
                !_stage.PrimaryLevel.CurrentLevelOptions.HasSquadActionBox)
            {
                return;
            }

            SquadActionBox actionBox = _stage.Menus.ActionBox;
            if (!_commandPresentationApplied)
            {
                ApplyProgressiveCommandPresentation(actionBox);
            }

            Gunship gunship = _stage.PrimaryLevel.State.GetHumanShips()
                .OfType<Gunship>()
                .FirstOrDefault(ship => ship != null && !ship.IsDead);
            if (gunship != null && gunship.Squad != null && gunship.Squad.AttackOnSight)
            {
                RestoreCommandPresentation();
                return;
            }

            PulseAttackOnSightCommand();
        }

        private void ApplyProgressiveCommandPresentation(SquadActionBox actionBox)
        {
            // Leave Cease Fire, Attack on Sight, Chase, and Match Speed visible so the player can
            // discover adjacent concepts, while withholding the more advanced command group until
            // the required engagement command has been used once.
            RememberAndHide(actionBox.PatrolButton);
            RememberAndHide(actionBox.GuardButton);
            RememberAndHide(actionBox.HoldButton);
            RememberAndHide(actionBox.LockOnButton);

            _attackOnSightButton = actionBox.AttackOnSightButton;
            if (_attackOnSightButton != null)
            {
                _attackOnSightBaseScale = _attackOnSightButton.transform.localScale;
            }
            _commandPresentationApplied = true;
        }

        private void RememberAndHide(GameObject command)
        {
            if (command == null || _hiddenCommandStates.ContainsKey(command))
            {
                return;
            }

            _hiddenCommandStates.Add(command, command.activeSelf);
            command.SetActive(false);
        }

        private void PulseAttackOnSightCommand()
        {
            if (_attackOnSightButton == null || !_attackOnSightButton.activeInHierarchy)
            {
                return;
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * CommandPulseSpeed);
            float scale = 1f + CommandPulseAmount * pulse;
            _attackOnSightButton.transform.localScale = _attackOnSightBaseScale * scale;
        }

        private void RestoreCommandPresentation()
        {
            if (!_commandPresentationApplied)
            {
                return;
            }

            foreach (KeyValuePair<GameObject, bool> pair in _hiddenCommandStates)
            {
                if (pair.Key != null)
                {
                    pair.Key.SetActive(pair.Value);
                }
            }
            _hiddenCommandStates.Clear();

            if (_attackOnSightButton != null)
            {
                _attackOnSightButton.transform.localScale = _attackOnSightBaseScale;
            }
            _attackOnSightButton = null;
            _commandPresentationApplied = false;
        }

        private void ResetPresentation()
        {
            RestoreCommandPresentation();

            if (_revealActive && _stage != null)
            {
                if (_stage.Camera != null && _revealStartZoom > 0f)
                {
                    _stage.Camera.orthographicSize = _revealStartZoom;
                }
                _stage.IsPlayerControlling = _restorePlayerControl;
            }

            _revealActive = false;
            _revealComplete = false;
            _revealHoneybee = null;
            _hasLastCameraPosition = false;
        }
    }
}
