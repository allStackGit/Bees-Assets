using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Normalized progress through the level timeout used by RL observations.
        /// Returns 0 before the episode timeout timer is armed and 1 at/after its deadline.
        /// </summary>
        internal float GetNormalizedRlEpisodeProgress()
        {
            if (!_hasSetTimeoutTimer || _timeoutTimer == null || _timeoutTimer.Length <= 0f)
            {
                return 0f;
            }

            return Mathf.Clamp01(_timeoutTimer.Elapsed / _timeoutTimer.Length);
        }
    }
}
