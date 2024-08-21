using Assets.Scripts.Entities.Ships.Weapons;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Ships
{
    public class Barge : Ship
    {
        public bool HasCompletedRun;
        /// <summary>
        /// Whether the ship has started the charge action
        /// </summary>
        public bool HasStartedCharging;
        public bool IsCharging;
        public int OriginalPower;
        public Weapon Charge => Weapons.First();



        protected override void UpdateDebugProperties()
        {
            base.UpdateDebugProperties();
        }

        protected override void OnTriggerEnter2D(Collider2D collider) // ship collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.name == "Selection Box")
            {
                //Debug.Log("Striker hit selection box");
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
            else if (collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                Ship hit = collidingThing.GetComponent<Ship>();

                if (hit.Side != Side && IsCharging)
                {
                    HitShip(hit);
                }
            }

        }


        public void HitShip(Ship ship)
        {
            int damage = math.min(Charge.Power, ship.Health);
            LogAttackingDamage(damage, this, ship);
            LogAttackingDamage((int)(damage * .75f), ship, this);
            Charge.Power -= damage;
            //Debug.Log($"{Name} hit {ship.Name} and did {damage} damage");

            if (Charge.Power == 0 || Level.GetState().GameOver) // if ran out of power or we killed the last ship stop the charge immediately
            {
                StopCharge();
            }
        }

        public IEnumerator ChargeForward()
        {
            OriginalPower = Charge.Power;

            StopMoving("Pausing to build up steam before charging");
            CannotChangeMovementOrders = true;
            //Debug.Log($"{Name} is about to charge");

            yield return new WaitForSeconds(2);

            if (!IsDead)
            {
                //Debug.Log($"Charging!");
                IsCharging = true;
                HasStartedCharging = true;
                CannotChangeMovementOrders = false;
                SetCurrentSpeed(80, 80);
                MoveInDirection(GetRotation());
                CannotChangeMovementOrders = true;
            }



            yield return new WaitForSeconds(1);
            StopCharge();

            //yield return new WaitForSeconds(1);
            //Debug.Log("1 second");
            //yield return new WaitForSeconds(1);
            //Debug.Log("2 seconds");
            //yield return new WaitForSeconds(1);
            //Debug.Log("3 seconds");
            //yield return new WaitForSeconds(1);
            //Debug.Log("4 seconds");
            //yield return new WaitForSeconds(1);
            yield return new WaitForSeconds(5);

            FinishCoolDown();

        }



        public void StopCharge() // [stats-method]
        {
            if (!IsDead)
            {
                IsCharging = false;
                SetCurrentSpeed(0, 0);
                StopMoving($"Finished charging");
                Charge.Power = OriginalPower;

                LogDamage(200);

                Debug.Log($"Stopped charging");
            }

        }

        public void FinishCoolDown()
        {
            if (!IsDead)
            {
                Debug.Log($"Finished cool down");
                HasStartedCharging = false;
                SetCurrentSpeed(Speed);
                HasCompletedRun = true;
                StopMoving($"Finished cool down");
                CannotChangeMovementOrders = false;
            }

        }
    }
}