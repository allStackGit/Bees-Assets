using Assets.Scripts;
using Assets.Scripts.UI_Components;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UIComponents
{
    public class LevelIntro : Assets.Scripts.Scenes.Scene
    {
        public CutsceneManager CutsceneManager;
        public GameObject ContinueButton, SkipButton;
        public Button ContinueButtonAction;
        public int LevelNumber;
        public int StandardDelay = 1;

        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();

            // The level-intro screen uses the ship ambience in place of menu music.
            UIAudioController.Instance?.PauseMusic();
            UIAudioController.Instance?.PlayLevelIntroAmbience();

            LevelNumber = ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide);

            CutsceneManager.Setup(() =>
            {
                ShowContinueButton();
            });
            CampaignDialogueOverrides.Apply(CutsceneManager);

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge && LevelNumber == 0)
            {
                CutsceneManager.PlayDialogueSection(CutsceneManager.StartedChallengeMode, true);

                ContinueButtonAction.onClick.AddListener(() =>
                {
                    SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
                });
                return;
            }

            if (ConfigData.CurrentShips.GetAliveFleetShipsBySide(ConfigData.Configuration.UserSide).Count == 0)
            {
                CutsceneManager.PlayDialogueSection(CutsceneManager.LostCampaign, true);
                ContinueButtonAction.onClick.AddListener(() =>
                {
                    SceneManager.LoadSceneAsync("Main Menu", LoadSceneMode.Single);
                });
                return;
            }

            ContinueButtonAction.onClick.AddListener(Continue);

            switch (LevelNumber)
            {
                case 1:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlayDialogueSection(CutsceneManager.PlutoLines_Reinforcements.GetRange(0, 2), true);
                    }));
                    break;
                case 2:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_Pushback[0], true);
                    }));
                    break;
                case 3:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_BluerPastures[0], true);
                    }));
                    break;
                case 4:
                    if (ConfigData.HasSeenIntermission)
                    {
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.Neptune_SeizeTheMeans.GetRange(0, 2), true);
                        }));
                    }
                    else
                    {
                        SkipButton.SetActive(true);
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.PlutoToNeptune, true);
                        }));
                    }
                    break;
                case 5:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlayDialogueSection(CutsceneManager.Neptune_OfProduction.GetRange(0, 2), true);
                    }));
                    break;
                case 6:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Neptune_PressingForward[0], true);
                    }));
                    break;
                case 7:
                    if (ConfigData.HasSeenIntermission)
                    {
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.Titania_Minesweeper.GetRange(0, 1), true);
                        }));
                    }
                    else
                    {
                        SkipButton.SetActive(true);
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.NeptuneToTitania, true);
                        }));
                    }
                    break;
                case 8:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Titania_Beenoculars[0], true);
                    }));
                    break;
                case 9:
                    if (ConfigData.HasSeenIntermission)
                    {
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.Uranus_OnTheOffensive.GetRange(0, 2), true);
                        }));
                    }
                    else
                    {
                        SkipButton.SetActive(true);
                        StartCoroutine(DelayStart(StandardDelay, () =>
                        {
                            CutsceneManager.PlayDialogueSection(
                                CampaignDialogueOverrides.BuildTitaniaToUranus(false), true);
                        }));
                    }
                    break;
                case 10:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Uranus_OnTheDefensive[0], true);
                    }));
                    break;
                case 11:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(StandardDelay, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Uranus_ANewThreat[0], true);
                    }));
                    break;
            }
        }

        private void OnDestroy()
        {
            UIAudioController.Instance?.StopLevelIntroAmbience();
        }

        public void ShowContinueButton()
        {
            SkipButton.SetActive(false);
            ContinueButton.SetActive(true);
        }

        public void Continue()
        {
            if (!ConfigData.HasSeenIntermission)
            {
                ConfigData.HasSeenIntermission = true;
            }
            else
            {
                ConfigData.HasSeenPreLevelIntro = true;
            }
            ConfigData.LoadLevel();
        }

        public void Skip()
        {
            CutsceneManager.HideDialogue();
            Continue();
        }

        private IEnumerator DelayStart(float delaySeconds, Action action)
        {
            yield return new WaitForSeconds(delaySeconds);
            action();
        }

        public void Back()
        {
            SceneManager.LoadSceneAsync("Main Menu", LoadSceneMode.Single);
        }
    }
}
