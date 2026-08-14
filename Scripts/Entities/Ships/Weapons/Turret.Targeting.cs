using System.Linq;
using Assets.Scripts.Levels;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public partial class Turret
    {
        private Ship _potentialTargetShip;

        private void TargetingSequence()
        {
            FreezeDiagnostics.RecordTurretTargetingPass(Level, ShipsWithinRange.Count);
            TargetingPasses++;
            if ((ReadyToFire && IsAimedAtTarget) || TargetingPasses == PassesPerFire)
            {
                TryToFire();
            }
            else if (!IsFiringManually)
            {
                if (!Ship.IsCeaseFire)
                {
                    Targeting();
                }
            }
            else
            {
                SetTargetShipNull();
            }

            if (TargetShip == null)
            {
                TryToFindAsteroidTarget();
            }
        }

        public void TryToFindAsteroidTarget()
        {
            TargetAsteroid = Ship.NearbyAsteroids.FirstOrDefault(asteroid => asteroid != null && !asteroid.IsDead);
            HasTargetAsteroid = TargetAsteroid != null;
        }

        protected void SetTargetShipNull()
        {
            TargetShip = null;
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            TargetShip = targetShip;
        }

        public bool HasValidTarget()
        {
            foreach (Ship ship in ShipsWithinRange.Values)
            {
                if (IsShipValidTarget(ship))
                {
                    return true;
                }
            }
            return false;
        }

        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return potentialTargetShip != null &&
                   !potentialTargetShip.IsDead &&
                   potentialTargetShip.Side != Side &&
                   IsShipWithinRange(potentialTargetShip) &&
                   potentialTargetShip.IsInBounds() &&
                   HasClearLineOfFire(potentialTargetShip);
        }

        protected void FireNext()
        {
            if (CachedTargetingQueue.Count == 0)
            {
                return;
            }

            if (!DetermineTargetShip(CachedTargetingQueue, true))
            {
                DetermineTargetShip(CachedTargetingQueue, false);
            }
            if (ShouldFire)
            {
                TryToFire();
            }
        }

        protected Ship GetAimedAtTarget()
        {
            foreach (Ship ship in CachedTargetingQueue)
            {
                if (IsShipValidTarget(ship) && Utilities.IsAimedAt(this, GetDegreesTowardsPoint(GetTargetPoint(ship))))
                {
                    return ship;
                }
            }
            return null;
        }

        protected void Fire()
        {
            Ship.SetCombatTimer();
            TargetShip.SetCombatTimer();
            SendProjectile();
            ReadyToFire = false;
        }

        protected void FireAtPoint()
        {
            Ship.SetCombatTimer();
            SendProjectile();
        }

        protected void TryToFire()
        {
            if (IsFiringManually || IsFiringAtAsteroid)
            {
                if (IsAimedAtTarget && !Ship.IsCeaseFire)
                {
                    FireAtPoint();
                }
            }
            else if (ShouldFire)
            {
                if (IsAimedAtTarget)
                {
                    Fire();
                }
                else
                {
                    _potentialTargetShip = GetAimedAtTarget();
                    if (_potentialTargetShip != null)
                    {
                        SetTargetShip(_potentialTargetShip);
                        Fire();
                    }
                    else
                    {
                        ReadyToFire = true;
                    }
                }
            }
            else if (TargetShip == null)
            {
                FireNext();
            }

            TargetingPasses = 0;
        }
    }
}
