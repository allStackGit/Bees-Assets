using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Scenes
{
    [DefaultExecutionOrder(-10000)]
    internal sealed class CampaignPresentationGuard : MonoBehaviour
    {
        private global::Stage _speedStage;
        private float _plutoOneTimeScale;
        private bool _hasPlutoOneSpeed;
        private bool _plutoOneSpeedRestored;

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
                ResetPlutoOneSpeedState();
                return;
            }

            // Preserve explicit ad-hoc negative-ID levels. Persisted campaign missions should not
            // inherit the old testing flag because Level.Reset uses it to bypass SetTriggers(),
            // which suppresses the mission's in-level dialogue and objective scripting.
            if (ConfigData.LevelOptions != null && ConfigData.LevelOptions.Id < 0)
            {
                ResetPlutoOneSpeedState();
                return;
            }

            ConfigData.IsTestingLevel = false;

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (CampaignMissionCatalog.IsCampaignComplete(missionId))
            {
                ResetPlutoOneSpeedState();
                return;
            }

            global::Stage stage = Object.FindObjectOfType<global::Stage>();
            if (stage == null)
            {
                return;
            }

            PreservePlutoOneGameSpeed(stage, missionId);

            CampaignMissionCatalog.MissionDefinition mission = CampaignMissionCatalog.Get(missionId);
            stage.HasRandomizedOptions = true;
            stage.OverrideMapIndex = mission.MapIndex;
            stage.GeneratedSquadCountOverride = 0;
            stage.GeneratedSquadCountMinimum = 0;
            stage.UseFullyRandomSquads = false;
            stage.UseFullyRandomEnemySquads = false;
        }

        private void PreservePlutoOneGameSpeed(global::Stage stage, int missionId)
        {
            if (_speedStage != stage)
            {
                _speedStage = stage;
                _hasPlutoOneSpeed = false;
                _plutoOneSpeedRestored = false;
            }

            if (missionId != 0 || stage.Menus == null)
            {
                if (missionId != 0)
                {
                    _hasPlutoOneSpeed = false;
                    _plutoOneSpeedRestored = false;
                }
                return;
            }

            Ship cameraShip = stage.CameraShip;
            if (cameraShip != null && !cameraShip.IsDead && stage.IsFollowingShip &&
                cameraShip.ShipType == ConfigData.ShipTypes.Scout)
            {
                // Dialogue temporarily owns the simulation speed after the scripted Scout leaves.
                // Keep the player's latest selection so that temporary cutscene state does not
                // become the Gunship's gameplay speed.
                _plutoOneTimeScale = Mathf.Clamp(stage.TimeScale, 1f, 2f);
                _hasPlutoOneSpeed = true;
                _plutoOneSpeedRestored = false;
                return;
            }

            if (_hasPlutoOneSpeed && !_plutoOneSpeedRestored && cameraShip != null &&
                !cameraShip.IsDead && stage.IsFollowingShip &&
                cameraShip.ShipType == ConfigData.ShipTypes.Gunship)
            {
                stage.TimeScale = _plutoOneTimeScale;
                Time.timeScale = stage.TimeScale;
                if (stage.Menus.GameSpeedButtonText != null)
                {
                    stage.Menus.GameSpeedButtonText.text = $"{stage.TimeScale}x";
                }
                _plutoOneSpeedRestored = true;
            }
        }

        private void ResetPlutoOneSpeedState()
        {
            _speedStage = null;
            _hasPlutoOneSpeed = false;
            _plutoOneSpeedRestored = false;
        }
    }
}
