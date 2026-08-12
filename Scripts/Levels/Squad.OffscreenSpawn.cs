using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        /// <summary>
        /// Places an intact saved formation at an intentional out-of-bounds center.
        /// This bypasses obstacle-aware startup placement, which is correct for normal
        /// in-map spawns but must not pull reinforcements back into an arbitrary valid
        /// compartment when the authored spawn is deliberately off screen.
        /// </summary>
        public void SetOffscreenStartingPosition(Vector2 center)
        {
            if (GetShips().Count == 1)
            {
                GetShips()[0].transform.localPosition = center;
                return;
            }

            foreach (Ship ship in GetShips())
            {
                Vector2 adjustment = GetFormationAdjustment(ship);
                ship.transform.localPosition = center + adjustment;
            }
        }
    }
}
