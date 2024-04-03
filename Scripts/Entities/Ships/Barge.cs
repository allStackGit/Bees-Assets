using Assets.Scripts.Entities.Ships.Weapons;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Ships
{
    public class Barge : Ship
    {
        public bool HasCompletedRun;
        public bool IsCharging;
        public HashSet<Ship> NearbyShips = new HashSet<Ship>();
        public Weapon Charge => Weapons.First();
        public int OriginalPower;
        public float OriginalSpeed;


        public List<string> __NearbyShips;

        protected override void UpdateDebugProperties()
        {
            __NearbyShips = NearbyShips.Select((s) => s.Name).ToList();
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
            else if (collidingThing.CompareTag("Ship") && ShipCollider.IsTouching(collider))
            {
                Ship hit = collidingThing.GetComponent<Ship>();
                //if (hit.Side != Side && !NearbyShips.Contains(hit))
                //{
                //    NearbyShips.Add(hit);
                //}
                //else if (NearbyShips.Contains(hit) && IsCharging)
                //{
                //    HitShip(hit);
                //}

                if (hit.Side != Side && IsCharging)
                {
                    HitShip(hit);
                }
            }

        }

        //protected override void OnTriggerExit2D(Collider2D collider) 
        //{
        //    GameObject collidingThing = collider.gameObject;
        //    if (collidingThing.CompareTag("Ship"))
        //    {
        //        Ship hit = collidingThing.GetComponent<Ship>();
        //        if (NearbyShips.Contains(hit))
        //        {
        //            NearbyShips.Remove(hit);
        //        }
        //    }

        //}

        public void HitShip(Ship ship)
        {
            int damage = math.min(Charge.Power, ship.Health);
            LogDamage(damage, this, ship);
            LogDamage((int)(damage * .75f), ship, this);
            Charge.Power -= damage;
            Debug.Log($"{Name} hit {ship.Name} and did {damage} damage");

            if (Charge.Power == 0)
            {
                StopCharge();
            }
        }

        public IEnumerator ChargeTarget()
        {
            Ship target = TargetShips.First();
            OriginalPower = Charge.Power;
            OriginalSpeed = Speed;

            IsCharging = true;
            StopMoving("Pausing to build up steam before charging");
            Debug.Log($"{Name} is about to charge {target.Name}");

            yield return new WaitForSeconds(2);

            if (!IsDead && target != null && !target.IsDead)
            {
                //Debug.Log($"Charging!");
                IsCharging = true;
                Speed = 80;
                SetCurrentSpeed(Speed);
                MoveToDirection(target.GetPosition());
            }



            yield return new WaitForSeconds(1);
            StopCharge();

            yield return new WaitForSeconds(5);
            FinishCoolDown();

        }

        public void StopCharge() // [stats-method]
        {
            if (!IsDead)
            {
                Speed = 0;
                SetCurrentSpeed(Speed);
                StopMoving("Finished charging");
                Charge.Power = OriginalPower;

                int oldTsv = Tsv;
                Health -= math.min(200, Health);


                int tsvChange = Tsv - oldTsv;
                FleetShip.DamageReceived += -tsvChange;
                Squad.SavedSquad.Stats.DamageReceived += -tsvChange;

                Squad.Command.Tsv += tsvChange; // subtract the TSV from the target

                //Debug.Log($"Finished charging");
            }

        }

        public void FinishCoolDown()
        {
            if (!IsDead)
            {
                //Debug.Log($"Finished cool down");
                IsCharging = false;
                Speed = OriginalSpeed;
                SetCurrentSpeed(Speed);
                HasCompletedRun = true;
                StopMoving("Finished cool down");
            }

        }
    }
}