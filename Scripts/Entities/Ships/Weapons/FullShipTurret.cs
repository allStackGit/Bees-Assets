using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// This is for turrets that don't rotate independently of the ship they're on, such as the flagship main cannon. Behaves just like a regular turret but the ship itself moves
    /// </summary>
    public class FullShipTurret : Turret
    {

        protected override void Aim()
        {
            if (!Ship.IsMoving)
            {
                base.Aim();
            }
            else
            {
                if (IsFiringManually)
                {
                    TargetPoint = Level.InputManager.GetMousePosition();
                }
                else if (ShouldFire)
                {
                    TargetPoint = GetTargetPoint(TargetShip);
                }
                AimedAtTarget = Utilities.IsRotatedTowards(Piece, GetDegreesTowardsPoint(TargetPoint));
            }

        }
    }
}