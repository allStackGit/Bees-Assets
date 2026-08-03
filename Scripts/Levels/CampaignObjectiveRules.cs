using System;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Pure campaign objective rules shared by mission trigger graphs and scenario
    /// tests. Keeping winner resolution independent of UI, dialogue, and persistence
    /// makes objective behavior deterministic and reusable by future level tooling.
    /// </summary>
    public static class CampaignObjectiveRules
    {
        public static int ResolveEliminationWinner(bool isUserSideKilled,
            bool isAiSideKilled, int userSide, int aiSide)
        {
            if (!isUserSideKilled && !isAiSideKilled)
            {
                throw new InvalidOperationException(
                    "An elimination winner cannot be resolved while both sides are alive.");
            }

            // Preserve the existing campaign rule: a simultaneous wipe is a player loss.
            return isUserSideKilled ? aiSide : userSide;
        }
    }
}
