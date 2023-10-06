
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes

{
    public class LoadingScreen : Scene
    {
        // Use this for initialization
        new void Start()
        {
            Name = "Loading Screen";
            base.Start();
            //Debugger.Log($"Started {Name} scene");
        }
        // Update is called once per frame
        new void Update()
        {
            base.Update();
            if (ConfigData.IsAllUserDataLoaded && ConfigData.AreAllSettingsLoaded && !ConfigData.Configuration.IsDeadVersion)
            {
                SceneManager.LoadScene("Main Menu");
            }
        }
    }
}