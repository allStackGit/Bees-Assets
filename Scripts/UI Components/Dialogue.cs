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


        public bool IsOpen => _dialogue != null && _dialogue.activeSelf;

        public Dialogue(GameObject prefab, string title, string explanation, List<string> buttonLabels, List<UnityAction> buttonActions)
        {
            //Debug.Log("Making dialogue");
            _dialogue = GameObject.Instantiate(prefab);
            _titleBox = _dialogue.transform.Find($"Main Panel/Text/Title").gameObject;
            _explanationBox = _dialogue.transform.Find($"Main Panel/Text/Explanation").gameObject;
            _buttonsContainer = _dialogue.transform.Find($"Main Panel/Buttons").gameObject;
            _buttonPrefab = _buttonsContainer.transform.Find($"Button Prefab").gameObject;


            _titleBox.GetComponent<TMP_Text>().text = title;
            _explanationBox.GetComponent<TMP_Text>().text = explanation;

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
                    MakeButton(buttonLabels[i], action);
                }
            }
            else
            {
                Debug.LogError($"{buttonLabels.Count} button labels given and {buttonActions.Count} button actions were given while making a new dialogue box");
            }
            GameObject.Destroy(_buttonPrefab);
            Hide();
        }
        private void MakeButton(string label, UnityAction action)
        {
            //Debug.Log($"Making button {label}");
            GameObject buttonObject = GameObject.Instantiate(_buttonPrefab);
            buttonObject.transform.SetParent(_buttonsContainer.transform, false);
            buttonObject.transform.localScale = Vector3.one;

            buttonObject.transform.GetChild(0).gameObject.GetComponent<TMP_Text>().text = label;
            buttonObject.GetComponent<Button>().onClick.AddListener(delegate ()
            {
                action();
                Hide();
            });

        }
        public void Show()
        {
            _dialogue.SetActive(true); 
        }  
        public void Hide()
        {
            _dialogue.SetActive(false);
        }
        public void SetTextBoxHeight(int height)
        {
            GameObject box = _dialogue.transform.Find($"Main Panel/Text").gameObject;
            RectTransform transform = box.GetComponent<RectTransform>();
            Vector2 size = transform.sizeDelta;
            transform.sizeDelta = new Vector2(size.x, height);
        }
    }
}