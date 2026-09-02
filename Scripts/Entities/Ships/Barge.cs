using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Barge : Ship
    {
        private const float ChargeBuildDelaySeconds = 2f;
        private const float ChargeDurationSeconds = 1f;
        private const float ChargeCooldownSeconds = 10f;
        private const float ChargeCycleSeconds = ChargeBuildDelaySeconds + ChargeDurationSeconds + ChargeCooldownSeconds;

        public bool HasCompletedRun;
        /// <summary>
        /// Existing gameplay/campaign flag. This retains its historical meaning: the wind-up has
        /// completed and the active charge is beginning. RL wind-up reservation is tracked separately.
        /// </summary>
        public bool HasStartedCharging, WaitingForNewCharge;
        public bool IsCharging;
        public int OriginalPower;
        public Weapon Charge;
        public ChargingBar ChargingBar;
        public GameObject BargeChargeAnimation;
        public GameObject BargeLoadingChargeAnimation;
        public GameObject BargeChargeImageAnimation;
        public BargeChargeImageAnimation BargeChargeImageAnimator;
        public List<GameObject> ChargeRocketFlares;
        private int _chargeLifecycleId;
        private int _chargePhase;
        private float _chargePhaseStartedAt;
        private readonly WaitForSeconds _chargeBuildDelay = new WaitForSeconds(ChargeBuildDelaySeconds);
        private readonly WaitForSeconds _chargeDuration = new WaitForSeconds(ChargeDurationSeconds);
        private readonly WaitForSeconds _chargeCooldown = new WaitForSeconds(ChargeCooldownSeconds);

        /// <summary>
        /// Stable RL phase channel: 0 ready, 1/3 wind-up, 2/3 active charge, 1 cooldown.
        /// </summary>
        internal float RlChargePhase => _chargePhase / 3f;

        /// <summary>
        /// True only when no wind-up, active charge, or cooldown is already reserved.
        /// </summary>
        internal bool IsRlChargeReady => _chargePhase == 0;

        /// <summary>
        /// Normalized scaled-game time until another charge can begin. Zero means ready.
        /// </summary>
        internal float RlChargeTimeUntilReadyFraction
        {
            get
            {
                if (_chargePhase == 0)
                {
                    return 0f;
                }

                float elapsed = Mathf.Max(0f, Time.time - _chargePhaseStartedAt);
                float remaining;
                switch (_chargePhase)
                {
                    case 1:
                        remaining = Mathf.Max(0f, ChargeBuildDelaySeconds - elapsed) +
                                    ChargeDurationSeconds + ChargeCooldownSeconds;
                        break;
                    case 2:
                        remaining = Mathf.Max(0f, ChargeDurationSeconds - elapsed) + ChargeCooldownSeconds;
                        break;
                    default:
                        remaining = Mathf.Max(0f, ChargeCooldownSeconds - elapsed);
                        break;
                }
                return Mathf.Clamp01(remaining / ChargeCycleSeconds);
            }
        }

        public override void Create(Stage stage)
        {
            base.Create(stage);
            if (IsUserControlled)
            {
                ChargingBar.Create(this, 10);
            }
            else
            {
                Destroy(ChargingBar.gameObject);
            }
            Charge = Weapons.First();
            OriginalPower = Charge.Power;

            if (Stage.IsTraining)
            {
                for (int i = 0; i < ChargeRocketFlares.Count; i++)
                {
                    Destroy(ChargeRocketFlares[i]);
                }
                ChargeRocketFlares.Clear();
                Destroy(BargeChargeAnimation);
                Destroy(BargeLoadingChargeAnimation);
                Destroy(BargeChargeImageAnimation);
            }
        }

        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            if (IsUserControlled)
            {
                ChargingBar.Setup();
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            _chargeLifecycleId++;
            ShipsHit.Clear();
            IsCharging = false;
            HasStartedCharging = false;
            WaitingForNewCharge = false;
            HasCompletedRun = false;
            HasWaitingTargetCoordinates = false;
            WaitingTargetCoordinates = Vector2.zero;
            SetChargePhase(0);
        }

        public override void Deactivate()
        {
            base.Deactivate();
            if (IsUserControlled)
            {
                ChargingBar.gameObject.SetActive(false);
            }
            if (!Stage.IsTraining)
            {
                ChargeRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(false);
                });
                BargeChargeAnimation.SetActive(false);
                BargeLoadingChargeAnimation.SetActive(false);
                BargeChargeImageAnimation.SetActive(false);
            }
        }

        public override void Activate()
        {
            base.Activate();
            if (IsUserControlled)
            {
                ChargingBar.gameObject.SetActive(true);
            }
        }

        public override void SetRocketFlares()
        {
            if (!IsCharging)
            {
                base.SetRocketFlares();
            }
            else
            {
                ChargeRocketFlares.ForEach((flare) =>
                {
                    flare.SetActive(true);
                });
            }
        }

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.name == "Selection Box")
            {
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            else if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (_collidingShip.Side != Side && IsCharging)
                {
                    HitShip(_collidingShip);
                }
            }
        }

        private GameObject _collidingThing;
        private Ship _collidingShip;
        protected void OnTriggerStay2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (_collidingShip.Side != Side && IsCharging)
                {
                    HitShip(_collidingShip);
                }
            }
        }

        public void HitShip(Ship ship)
        {
            if (!ShipsHit.Contains(ship))
            {
                ShipsHit.Add(ship);
                int damage = math.min(Charge.Power, ship.Health);
                LogAttackingDamage(damage, this, FleetShip, Squad.SavedSquad, ship);
                LogAttackingDamage((int)(damage * .75f), ship, ship.FleetShip, ship.Squad.SavedSquad, this);
                if (!Stage.IsTraining) Debug.Log($"{Name} hit {ship.Name} and did {damage} damage");

                if ((ship.Health > 0 || Level.State.GameOver) && gameObject.activeSelf)
                {
                    StartCoroutine(StopCharge(_chargeLifecycleId));
                }
            }
        }

        internal bool TryReserveCharge()
        {
            if (_chargePhase != 0 || IsCharging)
            {
                return false;
            }

            SetChargePhase(1);
            return true;
        }

        public IEnumerator ChargeForward(Ship target = null)
        {
            if (!TryReserveCharge())
            {
                yield break;
            }

            int lifecycleId = ++_chargeLifecycleId;
            StopMoving("Pausing to build up steam before charging");
            CannotChangeMovementOrders = true;

            if (!Stage.IsTraining)
            {
                BargeLoadingChargeAnimation.SetActive(true);
                Debug.Log($"{Name} is about to charge");
            }
            yield return _chargeBuildDelay;

            if (IsDead || lifecycleId != _chargeLifecycleId)
            {
                yield break;
            }

            if (!Stage.IsTraining)
            {
                Debug.Log($"{Name} is charging");
                BargeLoadingChargeAnimation.SetActive(false);
                BargeChargeAnimation.SetActive(true);
                BargeChargeImageAnimation.SetActive(true);
                BargeChargeImageAnimator.StartCharge();
            }
            HasStartedCharging = true;
            IsCharging = true;
            SetChargePhase(2);
            CannotChangeMovementOrders = false;
            SetCurrentSpeed(80, 80);

            // Scripted commands may supply a target and retain their historical auto-aim.
            // The RL primitive action must charge along the heading the policy established; it
            // may not outsource aiming to nearest-target script logic.
            if (!Stage.IsTrainingNueralNetwork && target != null && !target.IsDead)
            {
                MoveToDirectionOfPoint(target.GetPosition());
            }
            else
            {
                MoveInDirection(Rotation);
            }
            CannotChangeMovementOrders = true;

            yield return _chargeDuration;
            if (!IsDead && lifecycleId == _chargeLifecycleId)
            {
                StartCoroutine(StopCharge(lifecycleId));
            }
        }

        /// <summary>
        /// Immediately stops the current charge and initiates the cooldown.
        /// A negative lifecycle id means "the current charge" for external callers such as MapBorder.
        /// </summary>
        public IEnumerator StopCharge(int lifecycleId = -1)
        {
            if (lifecycleId < 0)
            {
                lifecycleId = _chargeLifecycleId;
            }
            if (lifecycleId != _chargeLifecycleId)
            {
                yield break;
            }

            if (IsCharging)
            {
                IsCharging = false;
                SetChargePhase(3);
                SetCurrentSpeed(0, 0);

                if (!Stage.IsTraining)
                {
                    BargeChargeAnimation.SetActive(false);
                    BargeChargeImageAnimator.Kill();
                    ChargeRocketFlares.ForEach((flare) => flare.SetActive(false));
                }

                StopMoving($"Finished charging");
                Charge.Power = OriginalPower;
                LogDamage(200);

                if (IsUserControlled)
                {
                    ChargingBar.DrainBar();
                }
                yield return _chargeCooldown;

                if (!IsDead && lifecycleId == _chargeLifecycleId)
                {
                    FinishCoolDown();
                }
            }
        }

        public void ResetCharge()
        {
            _chargeLifecycleId++;
            IsCharging = false;
            HasStartedCharging = false;
            CannotChangeMovementOrders = false;
            HasCompletedRun = true;
            SetCurrentSpeed(Speed);
            ShipsHit.Clear();
            SetChargePhase(0);

            if (!Stage.IsTraining)
            {
                BargeLoadingChargeAnimation.SetActive(false);
                BargeChargeAnimation.SetActive(false);
                BargeChargeImageAnimator.Kill();
                ChargeRocketFlares.ForEach((flare) => flare.SetActive(false));
            }

            StopMoving($"Charge command ended");
            Charge.Power = OriginalPower;
        }

        public void FinishCoolDown()
        {
            if (!IsDead)
            {
                HasStartedCharging = false;
                SetChargePhase(0);
                SetCurrentSpeed(Speed);
                HasCompletedRun = true;
                StopMoving($"Finished cool down");
                ShipsHit.Clear();
                CannotChangeMovementOrders = false;

                if (HasWaitingTargetCoordinates)
                {
                    MoveToPoint(WaitingTargetCoordinates);
                    HasWaitingTargetCoordinates = false;
                }
                if (WaitingForNewCharge)
                {
                    WaitingForNewCharge = false;
                    StartCoroutine(ChargeForward());
                }
            }
        }

        private void SetChargePhase(int phase)
        {
            _chargePhase = Mathf.Clamp(phase, 0, 3);
            _chargePhaseStartedAt = _chargePhase == 0 ? 0f : Time.time;
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            ResetCharge();
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
