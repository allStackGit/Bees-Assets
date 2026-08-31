using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    /// <summary>
    /// Narrow manual-control seam for the dedicated RL experiment. Normal gameplay never enables
    /// this path; when enabled the policy owns turret aim and the request to fire while the existing
    /// turret timer still enforces the weapon's authored rate of fire.
    /// </summary>
    public partial class Turret
    {
        public bool IsRlControlled { get; private set; }
        public bool RlFireRequested { get; private set; }
        public Vector2 RlTargetPoint { get; private set; }

        public void SetRlControl(Vector2 targetPoint, bool fireRequested)
        {
            IsRlControlled = true;
            // Existing projectile implementations use this flag to distinguish point-fire from
            // automatic TargetShip fire. Aim() itself has an RL branch before the mouse-input branch.
            IsFiringManually = true;
            RlTargetPoint = targetPoint;
            RlFireRequested = fireRequested;
        }

        public void ClearRlControl()
        {
            IsRlControlled = false;
            RlFireRequested = false;
            RlTargetPoint = Vector2.zero;
            IsFiringManually = false;
        }
    }
}
