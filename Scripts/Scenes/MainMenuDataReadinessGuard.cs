using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Prevents Main Menu actions that require server-backed user data from being invoked
    /// before Scene has finished loading and finalizing that data.
    /// </summary>
    internal sealed class MainMenuDataReadinessGuard : MonoBehaviour
    {
        private MainMenu _mainMenu;
        private Button _campaignButton;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Main Menu")
            {
                return;
            }

            MainMenu mainMenu = Object.FindObjectOfType<MainMenu>();
            if (mainMenu == null)
            {
                return;
            }

            MainMenuDataReadinessGuard guard = mainMenu.GetComponent<MainMenuDataReadinessGuard>();
            if (guard == null)
            {
                guard = mainMenu.gameObject.AddComponent<MainMenuDataReadinessGuard>();
            }
            guard.Initialize(mainMenu);
        }

        private void Initialize(MainMenu mainMenu)
        {
            _mainMenu = mainMenu;
            _campaignButton = mainMenu.HumanCampaignModeButton != null
                ? mainMenu.HumanCampaignModeButton.GetComponent<Button>()
                : null;

            // Scene objects can receive UI events before Scene.Update has finished the asynchronous
            // settings/user-data bootstrap. Disable the action immediately on scene load.
            if (_campaignButton != null)
            {
                _campaignButton.enabled = false;
            }
        }

        private void Update()
        {
            if (_mainMenu == null || _campaignButton == null)
            {
                Destroy(this);
                return;
            }

            if (!ConfigData.AreAllSettingsLoaded || !ConfigData.IsAllUserDataLoaded ||
                ConfigData.UserProgressData == null || ConfigData.CampaignShips == null ||
                ConfigData.GetCampaignLevelData() == null || !_mainMenu.IsFinalized)
            {
                _campaignButton.enabled = false;
                return;
            }

            int currentLevel = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.HumanSide,
                ConfigData.GameModes.Campaign);

            // Preserve the normal completed/reset campaign states established by MainMenu finalization.
            _campaignButton.enabled = !_mainMenu.IsResettingCampaign &&
                !CampaignMissionCatalog.IsCampaignComplete(currentLevel);
            Destroy(this);
        }
    }
}
