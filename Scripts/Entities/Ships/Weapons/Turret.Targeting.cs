using System.Collections.Generic;
using System.Linq;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public partial class Turret
    {
        private int _index;
        private IEnumerable<Ship> _shipsWithinRange;
        private Ship _potentialTargetShip;

        private void TargetingSequence()
        {
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
            if (Ship.NearbyAsteroids.Count > 0)
            {
                TargetAsteroid = Ship.NearbyAsteroids[0];
                HasTargetAsteroid = true;
            }
            else
            {
                HasTargetAsteroid = false;
            }
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
            _shipsWithinRange = ShipsWithinRange.Values;
            for (_index = 0; _index < ShipsWithinRange.Count; _index++)
            {
                if (IsShipValidTarget(_shipsWithinRange.ElementAt(_index)))
                {
                    return true;
                }
            }
            return false;
        }

        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead &&
                   IsShipWithinRange(potentialTargetShip) &&
                   potentialTargetShip.IsInBounds() &&
                   (!Level.HasObstacles || !Utilities.HasObstaclesInTheWay(GetPosition(), GetTargetPoint(potentialTargetShip)));
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
                if (!ship.IsDead && Utilities.IsAimedAt(this, GetDegreesTowardsPoint(GetTargetPoint(ship))))
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
