

using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Turret : Weapon
    {
        public bool FireAtFrontOfShip, AimedAtTarget;
        public int TargetingPasses, PassesPerFire;
        /// <summary>
        /// How many times the targeting sequence runs per fire sequence
        /// </summary>
        public float TargetingRate;
        public float DamagePerSecond => RateOfFire > 0 ? (Power / RateOfFire) : 0;
        public float RotationRate => Ship.Speed * 16;


        public virtual void Setup(Ship ship, int range, int power, float rateOfFire, float projectileValue, GameObject piece,
            GameObject projectilePrefab, bool fireAtFrontOfShip)
        {
            base.Setup(ship, range, power, 0, rateOfFire, projectileValue, piece, projectilePrefab);
            FireAtFrontOfShip = fireAtFrontOfShip;
            PassesPerFire = 3;
            TargetingRate = RateOfFire / PassesPerFire;

        }
        private void Start()
        {
            if (RateOfFire > 0)
            {
                //Debugger.Log($"Aiming rate: {TargetingRate} for {FleetShip.Name}");
                InvokeRepeating(nameof(TargetingSequence), TargetingRate, TargetingRate);
                //Invoke(nameof(Fire), RateOfFire);
            }
        }
        private void FixedUpdate()
        {
            Aim();
        }
        private void TargetingSequence()
        {
            __NotShootingReason = $"Reset Reason.";
            TargetingPasses++;
            if (TargetingPasses == PassesPerFire)
            {
                //Debugger.Log($"Hit {TargetingPasses} targeting passes, now firing");
                Fire();
                TargetingPasses = 0;
            }
            else
            {
                Targeting();
            }
        }
        protected virtual void Aim()
        {
            if (TargetShip != null && !CeaseFire)
            {
                //Debugger.Log($"Aiming {Piece.name}");
                Vector2 targetPoint = TargetShip.GetPosition();
                if (FireAtFrontOfShip)
                {
                    Vector2 frontOfShip = targetPoint + new Vector2(0, TargetShip.GetHalfHeight() - ConfigData.OffsetFromFront);
                    targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);

                }
                AimedAtTarget = Utilities.TimedRotation(Piece, GetDegreesTowardsPoint(targetPoint), RotationRate);

            }
            else
            {
                AimedAtTarget = false;
                Utilities.TimedRotation(Piece, Ship.GetRotation(), RotationRate);
            }

        }
        protected override void SendProjectile() // [projectile-method] [note]
        {
            base.SendProjectile();
            //Debugger.Log("Sending turret projectile");
            Vector2 targetPoint = TargetShip.GetPosition();
            if (FireAtFrontOfShip)
            {
                Vector2 frontOfShip = targetPoint + new Vector2(0, TargetShip.GetHalfHeight() - ConfigData.OffsetFromFront);
                targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);

            }
            float angle = AngleToPoint(targetPoint);

            Level.AddProjectile(ProjectilePrefab, this, GetPosition(), angle);
            Ship.FleetShip.ShotsFired++;

        }
        protected void Fire()
        {
            if (TargetShip != null && !TargetShip.IsDead && !CeaseFire)
            {
                if (AimedAtTarget)
                {
                    //Debugger.Log($"{Ship.Name} is firing at {TargetShip.Name}");
                    Ship.SetCombatTimer();

                    TargetShip.SetCombatTimer();

                    SendProjectile();
                    //Invoke(nameof(Fire), RateOfFire);
                }
                else
                {
                    //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because the piece is not aimed at the target: {TargetShip.Name}");
                    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because the piece is not aimed at the target: {TargetShip.Name}";
                    Invoke(nameof(Fire), .1f);
                }

            }
            else
            {
                if (TargetShip == null)
                {
                    //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because the TargetShip is null");
                    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because the TargetShip is null";
                }
                else if (TargetShip.IsDead)
                {
                    //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because the TargetShip is dead");
                    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because the TargetShip is dead";

                }
                else if (CeaseFire)
                {
                    //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because CeaseFire is on");
                    __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because CeaseFire is on";
                }
                //Invoke(nameof(Fire), RateOfFire);
            }

        }

    }
}