using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Prevents generic/testing Stage inspector overrides from replacing authored campaign data.
    /// Campaign progress/catalog data owns mission identity and map selection; Squad Maker owns
    /// only the player's chosen squads.
    /// </summary>
    internal static class CampaignStageConfiguration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null ||
                ConfigData.Configuration == null)
            {
                return;
            }

            // Campaign test mode intentionally uses an ad-hoc negative-ID LevelOptions object.
            // It is not the persisted campaign mission and must retain its selected map/options.
            if (ConfigData.LevelOptions != null && ConfigData.LevelOptions.Id < 0)
            {
                return;
            }

            // sceneLoaded runs before the normal Stage/Level startup path. Clear any stale testing
            // state here as a second boundary so direct campaign scene loads cannot reach
            // Level.SetupLevel with IsTestingLevel=true and silently suppress SetTriggers/dialogue.
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

            // Stage's generic fixed-map path uses OverrideMapIndex (default 0/Pluto). Campaign
            // levels instead need to consume their authored LevelOptions values for map, obstacles,
            // asteroids, fog, mining, and enemy setup.
            stage.HasRandomizedOptions = true;
            stage.OverrideMapIndex = mission.MapIndex;
            stage.GeneratedSquadCountOverride = 0;
            stage.GeneratedSquadCountMinimum = 0;
            stage.UseFullyRandomSquads = false;
            stage.UseFullyRandomEnemySquads = false;

            if (ConfigData.LevelOptions != null && ConfigData.LevelOptions.Id != missionId)
            {
                Debug.LogError($"Campaign Stage loaded with level options #{ConfigData.LevelOptions.Id} ({ConfigData.LevelOptions.Name}) while progress is mission #{missionId} ({mission.Name}).");
            }
        }
    }
}
