

using Assets.Scripts;
using System;
using System.Collections;
using UnityEngine;

namespace UIComponents
{
    public class LevelIntro : Assets.Scripts.Scenes.Scene
    {
        public CutsceneManager CutsceneManager;
        public GameObject ContinueButton;
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
                case 0:
                case 1:
                    Debug.Log("Playing Pluto reinforcements dialogue.");
                    
                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_Reinforcements[0], true);
                    }));
                    break;
                case 2:
                    Debug.Log("Playing Pluto Bluer pastures dialogue.");

                    StartCoroutine(DelayStart(3, () =>
                    {
                        CutsceneManager.PlaySingleDialogueLine(CutsceneManager.PlutoLines_BluerPastures[0], true);
                    }));
                    break;
            }
        }

        public void ShowContinueButton()
        {
            ContinueButton.SetActive(true);
        }

        public void Continue()
        {
            ConfigData.HasSeenPreLevelIntro = true;
            ConfigData.LoadLevel();
        }

        private IEnumerator DelayStart(float delaySeconds, Action action)
        {
            yield return new WaitForSeconds(delaySeconds);
            action();
        }

    }


}
