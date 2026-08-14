using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Scenes
{
    [DefaultExecutionOrder(-10000)]
    internal sealed class CampaignPresentationGuard : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Campaign Presentation Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignPresentationGuard>();
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null)
            {
                return;
            }

            // Preserve explicit ad-hoc negative-ID levels. Persisted campaign missions should not
            // inherit the old testing flag because Level.Reset uses it to bypass SetTriggers(),
            // which suppresses the mission's in-level dialogue and objective scripting.
            if (ConfigData.LevelOptions != null && ConfigData.LevelOptions.Id < 0)
            {
                return;
            }

            ConfigData.IsTestingLevel = false;

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (CampaignMissionCatalog.IsCampaignComplete(missionId))
            {
                return;
            }

            global::Stage stage = Object.FindObjectOfType<global::Stage>();
            if (stage == null)
            {
                return;
            }

            CampaignMissionCatalog.MissionDefinition mission = CampaignMissionCatalog.Get(missionId);
            stage.HasRandomizedOptions = true;
            stage.OverrideMapIndex = mission.MapIndex;
            stage.GeneratedSquadCountOverride = 0;
            stage.GeneratedSquadCountMinimum = 0;
            stage.UseFullyRandomSquads = false;
            stage.UseFullyRandomEnemySquads = false;
        }
    }
}
