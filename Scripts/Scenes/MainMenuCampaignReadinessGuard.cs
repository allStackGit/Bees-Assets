using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Prevents campaign entry before the Main Menu has finished loading all server-backed
    /// settings/user data and constructing the campaign fleet. MainMenu finalization is the
    /// authoritative readiness boundary for gameplay actions.
    /// </summary>
    internal sealed class MainMenuCampaignReadinessGuard : MonoBehaviour
    {
        private const string MainMenuScene = "Main Menu";
        private MainMenu _mainMenu;
        private Button _campaignButton;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject guardObject = new GameObject(nameof(MainMenuCampaignReadinessGuard));
            DontDestroyOnLoad(guardObject);
            guardObject.AddComponent<MainMenuCampaignReadinessGuard>();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            _mainMenu = null;
            _campaignButton = null;

            if (scene.name != MainMenuScene)
            {
                return;
            }

            _mainMenu = FindFirstObjectByType<MainMenu>();
            if (_mainMenu == null || _mainMenu.HumanCampaignModeButton == null)
            {
                return;
            }

            _campaignButton = _mainMenu.HumanCampaignModeButton.GetComponent<Button>();
            if (_campaignButton != null)
            {
                _campaignButton.interactable = false;
            }
        }

        private void Update()
        {
            if (_mainMenu == null || _campaignButton == null || !_mainMenu.IsFinalized)
            {
                return;
            }

            bool campaignComplete = false;
            if (ConfigData.UserProgressData != null && ConfigData.Configuration != null)
            {
                int currentLevel = ConfigData.UserProgressData.GetCurrentLevel(
                    ConfigData.Configuration.HumanSide,
                    ConfigData.GameModes.Campaign);
                campaignComplete = CampaignMissionCatalog.IsCampaignComplete(currentLevel);
            }

            _campaignButton.interactable = !campaignComplete;
            _mainMenu = null;
            _campaignButton = null;
        }
    }
}
