using System;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

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

    public partial class Level
    {
        /// <summary>
        /// Runs Neptune I with a small continuation component for its legacy two-part success
        /// dialogue. The second section is queued after CloseLevel, when normal Level trigger
        /// polling has already stopped.
        /// </summary>
        public void Neptune1SeizeTheMeansWithEndingContinuation()
        {
            Neptune1SeizeTheMeans();
            Neptune1EndingContinuation continuation = gameObject.GetComponent<Neptune1EndingContinuation>();
            if (continuation == null)
            {
                continuation = gameObject.AddComponent<Neptune1EndingContinuation>();
            }
            continuation.Level = this;
        }

        /// <summary>
        /// Re-applies Uranus I's authored fog flag after the legacy environment setup path.
        /// The mission data requests fog even when the generic Stage controller flag was false.
        /// </summary>
        public void Uranus1OnTheOffensiveWithAuthoredFog()
        {
            Uranus1OnTheOffensive();
            if (!HasPlayer || CurrentLevelOptions == null || CurrentLevelOptions.FogOfWar != 1 ||
                Map == null || Map.FogOfWar == null || State == null)
            {
                return;
            }

            ActivateFogOfWar = true;
            Map.FogOfWar.SetActive(true);
            foreach (Ship ship in State.GetShips(ConfigData.Configuration.UserSide))
            {
                if (ship.HasUserFogOfWarVision)
                {
                    ship.FogOfWarVision.Activate();
                }
            }
        }
    }

    internal sealed class Neptune1EndingContinuation : MonoBehaviour
    {
        private const string ContinuationName = "Level 4 Post-success dialogue";
        internal Level Level;

        private void Update()
        {
            if (Level == null || ConfigData.Configuration == null || Level.IsLevelConnectedToServer ||
                Level.WinningSide != ConfigData.Configuration.UserSide || Level.Stage == null ||
                Level.Stage.CutsceneManager == null || !Level.Stage.CutsceneManager.HitDialogueBreak)
            {
                return;
            }

            Trigger continuation = Level.NextTriggers.Find(trigger =>
                trigger != null && trigger.Name == ContinuationName);
            if (continuation == null || !continuation.Conditional())
            {
                return;
            }

            Level.NextTriggers.Remove(continuation);
            continuation.Action();
            enabled = false;
        }
    }
}
