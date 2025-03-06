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
        public HashSet<Ship> ShipsHit = new HashSet<Ship>();
        public ChargingBar ChargingBar;
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
            Charge = Weapons.First();            //IsBomber = true;

            //Destroy(Charge.Piece);
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
            ShipsHit.Clear();
            IsCharging = false;
            HasStartedCharging = false;
            WaitingForNewCharge = false;
            HasCompletedRun = false;
        }
        public override void Deactivate()
        {
            base.Deactivate();
            if (IsUserControlled)
            {
                ChargingBar.gameObject.SetActive(false);
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


        protected override void OnTriggerEnter2D(Collider2D collider) // ship collision
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.name == "Selection Box")
            {
                //Debug.Log("Striker hit selection box");
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
                LogAttackingDamage((int)(damage * .75f), ship, ship.FleetShip, ship.Squad.SavedSquad, this); // Barge takes 75% of the damage it inflicts
                //Charge.Power -= damage;
                //Debug.Log($"{Name} hit {ship.Name} and did {damage} damage");

                if ((ship.Health > 0 || Level.State.GameOver) && gameObject.activeSelf) // if ran out of power or we killed the last ship stop the charge immediately
                {
                    StartCoroutine(StopCharge());
                }
            }

        }

        public IEnumerator ChargeForward(Ship target = null)
        {
            OriginalPower = Charge.Power;

            StopMoving("Pausing to build up steam before charging");
            CannotChangeMovementOrders = true;
            //Debug.Log($"{Name} is about to charge");

            yield return new WaitForSeconds(2);

            if (!IsDead)
            {
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
            }


            yield return new WaitForSeconds(1);
            if (!IsDead)
            {
                StartCoroutine(StopCharge());
            }

        }


        /// <summary>
        /// Immediately stops the movement of the barge and initiates the cooldown after a five second delay
        /// </summary>
        /// <returns></returns>
        public IEnumerator StopCharge() // [stats-method]
        {
            if (IsCharging)
            {
                IsCharging = false;
                SetCurrentSpeed(0, 0);
                StopMoving($"Finished charging");
                Charge.Power = OriginalPower;

                LogDamage(200);

                //Debug.Log($"Stopped charging for {Name}");
                if (IsUserControlled)
                {
                    ChargingBar.DrainBar();
                }
                yield return new WaitForSeconds(10);

                FinishCoolDown();
            }

        }

        public void FinishCoolDown()
        {
            if (!IsDead)
            {
                //Debug.Log($"Finished cool down for {Name}");
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
    }
}