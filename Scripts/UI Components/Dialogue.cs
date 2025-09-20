using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    public class Dialogue
    {
        private GameObject _dialogue, _titleBox, _explanationBox, _buttonsContainer, _buttonPrefab;
        private List<GameObject> _buttons = new List<GameObject>();
        private float _buttonPrefabHeight;
        private TMP_Text _explanationText, _titleText;


        public bool IsOpen => _dialogue != null && _dialogue.activeSelf;

        public Dialogue(GameObject prefab, string title, string explanation, List<string> buttonLabels, List<UnityAction> buttonActions)
        {
            //Debug.Log("Making dialogue");
            _dialogue = GameObject.Instantiate(prefab);
            _titleBox = _dialogue.transform.Find($"Main Panel/Text/Title").gameObject;
            _explanationBox = _dialogue.transform.Find($"Main Panel/Text/Explanation").gameObject;
            _buttonsContainer = _dialogue.transform.Find($"Main Panel/Buttons").gameObject;
            _buttonPrefab = _buttonsContainer.transform.Find($"Button Prefab").gameObject;
            _buttonPrefabHeight = _buttonPrefab.GetComponent<RectTransform>().sizeDelta.y;

            _titleText = _titleBox.GetComponent<TMP_Text>();
            _explanationText = _explanationBox.GetComponent<TMP_Text>();

            SetTitle(title);
            SetExplanation(explanation);

            if (buttonLabels.Count == buttonActions.Count || buttonLabels.Count - buttonActions.Count == 1)
            {
                for (int i = 0; i < buttonLabels.Count; i++)
                {
                    UnityAction action = null;
                    if (i < buttonActions.Count)
                    {
                        action = buttonActions[i];
                    }
                    else
                    {
                        action = Hide;
                    }
                    //Debug.Log($"Adding button #{i}");
                    _buttons.Add(MakeButton(buttonLabels[i], action));
                }
            }
            else
            {
                Debug.LogError($"{buttonLabels.Count} button labels given and {buttonActions.Count} button actions were given while making a new dialogue box");
            }
            //GameObject.Destroy(_buttonPrefab);
            _buttonPrefab.SetActive(false);
            Hide();
        }
        private GameObject MakeButton(string label, UnityAction action)
        {
            //Debug.Log($"Making button {label}");
            GameObject buttonObject = GameObject.Instantiate(_buttonPrefab);
            buttonObject.transform.SetParent(_buttonsContainer.transform, false);
            buttonObject.transform.localScale = Vector3.one;

            buttonObject.transform.GetChild(0).gameObject.GetComponent<TMP_Text>().text = label;
            buttonObject.GetComponent<Button>().onClick.AddListener(delegate ()
            {
                UIAudioController.Instance.PlayButtonSound();
                action();
                Hide();
            });
            buttonObject.SetActive(true);
            return buttonObject;

        }
        public void Show()
        {
            _dialogue.SetActive(true); 
        }  
        public void Hide()
        {
            _dialogue.SetActive(false);
        }
        public void SetButtonWidth(int index, int widthScale)
        {
            _buttons[index].GetComponent<RectTransform>().sizeDelta = new Vector2(widthScale, _buttonPrefabHeight);
        }
        public void SetTextBoxHeight(int height)
        {
            GameObject box = _dialogue.transform.Find($"Main Panel/Text").gameObject;
            RectTransform transform = box.GetComponent<RectTransform>();
            Vector2 size = transform.sizeDelta;
            transform.sizeDelta = new Vector2(size.x, height);
        }
        public void SetTitle(string title)
        {
            _titleText.text = title;
        }
        public void SetExplanation(string explanation)
        {
            _explanationText.text = explanation;
        }
        public void ChangeButton(int index, string label, UnityAction action)
        {
            GameObject.Destroy(_buttons[index]);
            _buttons[index] = MakeButton(label, action);

        }
    }
}