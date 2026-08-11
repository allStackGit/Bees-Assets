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
        public bool HasCompletedRun;
        /// <summary>
        /// Whether the ship has started the charge action
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
        }

        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            if (IsUserControlled)
            {
                ChargingBar.Setup();
            }
            if (Stage.IsTraining)
            {
                ChargeRocketFlares.ForEach((flare) =>
                {
                    Destroy(flare);
                });
                ChargeRocketFlares.Clear();
                Destroy(BargeChargeAnimation);
                Destroy(BargeLoadingChargeAnimation);
                Destroy(BargeChargeImageAnimation);
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
                Debug.Log($"{Name} hit {ship.Name} and did {damage} damage");

                if ((ship.Health > 0 || Level.State.GameOver) && gameObject.activeSelf)
                {
                    StartCoroutine(StopCharge(_chargeLifecycleId));
                }
            }
        }

        public IEnumerator ChargeForward(Ship target = null)
        {
            if (!IsCharging)
            {
                int lifecycleId = ++_chargeLifecycleId;
                StopMoving("Pausing to build up steam before charging");
                CannotChangeMovementOrders = true;

                if (!Stage.IsTraining)
                {
                    BargeLoadingChargeAnimation.SetActive(true);
                }

                Debug.Log($"{Name} is about to charge");
                yield return new WaitForSeconds(2);

                if (IsDead || lifecycleId != _chargeLifecycleId)
                {
                    yield break;
                }

                Debug.Log($"{Name} is charging");
                if (!Stage.IsTraining)
                {
                    BargeLoadingChargeAnimation.SetActive(false);
                    BargeChargeAnimation.SetActive(true);
                    BargeChargeImageAnimation.SetActive(true);
                    BargeChargeImageAnimator.StartCharge();
                }
                IsCharging = true;
                HasStartedCharging = true;
                CannotChangeMovementOrders = false;
                SetCurrentSpeed(80, 80);
                if (target != null && !target.IsDead)
                {
                    MoveToDirectionOfPoint(target.GetPosition());
                }
                else
                {
                    MoveInDirection(Rotation);
                }
                CannotChangeMovementOrders = true;

                yield return new WaitForSeconds(1);
                if (!IsDead && lifecycleId == _chargeLifecycleId)
                {
                    StartCoroutine(StopCharge(lifecycleId));
                }
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
                yield return new WaitForSeconds(10);

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

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            ResetCharge();
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
