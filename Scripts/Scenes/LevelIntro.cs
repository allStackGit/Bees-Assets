

using Assets.Scripts;
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

        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();
            //ConfigData.CurrentGameMode = ConfigData.GameModes.Campaign; // [debug] Temporary, for testing purposes only
            LevelNumber = ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide);

            CutsceneManager.Setup(() =>
            {
                ShowContinueButton();
            });

            //LevelNumber = 6; // [debug]

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge && LevelNumber == 0)
            {
                StartCoroutine(DelayStart(3, () =>
                {
                    CutsceneManager.PlayDialogueSection(CutsceneManager.StartedChallengeMode, true);
                }));
                
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
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_Reinforcements[0], true);
                    }));
                    break;
                case 2:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_BluerPastures[0], true);
                    }));
                    break;
                case 3:

                    if (ConfigData.HasSeenIntermission)
                    {
                        StartCoroutine(DelayStart(3, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.Neptune_SeizeTheMeans.GetRange(0, 2), true);
                        }));
                    }
                    else
                    {
                        SkipButton.SetActive(true);
                        StartCoroutine(DelayStart(3, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.PlutoToNeptune, true);
                        }));
                    }
                    break;
                case 4:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlayDialogueSection(CutsceneManager.Neptune_OfProduction.GetRange(0, 2), true);
                    }));
                    break;
                case 5:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Neptune_PressingForward[0], true);
                    }));
                    break;
                case 6:

                    if (ConfigData.HasSeenIntermission)
                    {
                        StartCoroutine(DelayStart(3, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.Uranus_OnTheOffensive.GetRange(0, 2), true);
                        }));
                    }
                    else
                    {
                        SkipButton.SetActive(true);
                        StartCoroutine(DelayStart(3, () =>
                        {
                            CutsceneManager.PlayDialogueSection(CutsceneManager.NeptuneToUranus, true);
                        }));
                    }
                    break;
                case 7:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Uranus_OnTheDefensive[0], true);
                    }));
                    break;
                case 8:
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.Uranus_ANewThreat[0], true);
                    }));
                    break;
            }
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

    }


}
