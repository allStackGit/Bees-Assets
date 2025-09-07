

using Assets.Scripts;
using System;
using System.Collections;
using UnityEngine;

namespace UIComponents
{
    public class LevelIntro : Assets.Scripts.Scenes.Scene
    {
        public CutsceneManager CutsceneManager;
        public GameObject ContinueButton, SkipButton;
        public int LevelNumber;

        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();
            //ConfigData.CurrentGameMode = ConfigData.GameModes.Campaign; // [alert] Temporary, for testing purposes only
            LevelNumber = ConfigData.UserProgressData.GetCurrentLevel();

            CutsceneManager.Setup(() =>
            {
                ShowContinueButton();
            });

            switch (LevelNumber)
            {
                case 1:
                    Debug.Log("Playing Pluto reinforcements dialogue.");
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_Reinforcements[0], true);
                    }));
                    break;
                case 2:
                    Debug.Log("Playing Pluto Bluer pastures dialogue.");
                    ConfigData.HasSeenIntermission = true;
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_BluerPastures[0], true);
                    }));
                    break;
                case 3:
                    Debug.Log("Playing Neptune Sieze the Means dialogue.");

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
            }
        }

        public void ShowContinueButton()
        {
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
