
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

using Assets.Scripts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using UnityEngine.UI;
using TMPro;
using Assets.Scripts.UIComponents;

namespace UIComponents
{
    public class LevelIntro : MonoBehaviour
    {
        public GameObject EventSystem, Portrait, PortraitTitle, MessageTitle,  Message;
        private List<LevelIntroMessage>  _messages = new List<LevelIntroMessage>();
        private int _level = ConfigData.GetUserProgressData().CurrentLevel;
        void Awake()
        {
            //ConfigData.SetupUserData();
            LoadAndSetText();
        }
        public void LoadAndSetText()
        {
            // load json file class
            TextAsset intros = (TextAsset)Resources.Load("LevelIntros");

            // load messages
            Utilities.JArrayToList<dynamic>((JArray)JsonConvert.DeserializeObject(intros.text)).ForEach((intro) =>
            {

                Sprite sprite = Resources.Load<Sprite>($"{ConfigData.PortraitFolder}/{intro.officer.name}");

                Officer officer = new Officer((string)intro.officer.name, (string)intro.officer.rank, sprite);

                _messages.Add(new LevelIntroMessage((string)intro.name, (string)intro.title, (string)intro.message, (int)intro.levelId, officer));
            });
            // get message based on level id
            LevelIntroMessage intro = _messages.Find((i) => i.LevelId == _level);

            // update game objects
            Debug.Log($"Loading info for level #{_level}, {intro.Name}");
            TMP_Text messageTitle = MessageTitle.GetComponentInChildren<TMP_Text>();
            TMP_Text message = Message.GetComponentInChildren<TMP_Text>();
            TMP_Text portraitTitle = PortraitTitle.GetComponentInChildren<TMP_Text>();
            Image sprite = Portrait.GetComponentInChildren<Image>();

            messageTitle.text = intro.Title;
            message.text = intro.Message;
            portraitTitle.text = $"{intro.Officer.Rank} {intro.Officer.Name}";
            sprite.sprite = intro.Officer.Portrait;
        }
        public void ContinueToLevel()
        {
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }
        public void DeselectButton()
        {
            EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        }
    }


}
