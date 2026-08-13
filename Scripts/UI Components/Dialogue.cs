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
        private readonly bool _playErrorSoundOnShow;
        private readonly bool _playSaveSoundOnShow;

        public bool IsOpen => _dialogue != null && _dialogue.activeSelf;

        public Dialogue(GameObject prefab, string title, string explanation, List<string> buttonLabels, List<UnityAction> buttonActions, bool playErrorSoundOnShow = false)
        {
            _playErrorSoundOnShow = playErrorSoundOnShow;
            // SquadSavingStatus is currently the only status-only Dialogue. Treat a Dialogue
            // with no actions/buttons as completion feedback rather than a clickable prompt.
            // Keep this at the UI-success boundary instead of SaveSquadData/SaveFleetData:
            // those persistence methods are also used by automatic campaign saves.
            _playSaveSoundOnShow = buttonLabels.Count == 0 && buttonActions.Count == 0;

            _dialogue = GameObject.Instantiate(prefab);
            // Prefabs are serialized active. Hide the clone before touching any child hierarchy so
            // a malformed/stale prefab or unexpected constructor exception can never leave an
            // unconfigured modal blocking the scene. Show() is the only path that makes it visible.
            _dialogue.SetActive(false);

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
                    UnityAction action = i < buttonActions.Count ? buttonActions[i] : Hide;
                    _buttons.Add(MakeButton(buttonLabels[i], action));
                }
            }
            else
            {
                Debug.LogError($"{buttonLabels.Count} button labels given and {buttonActions.Count} button actions were given while making a new dialogue box");
            }
            _buttonPrefab.SetActive(false);
        }

        private GameObject MakeButton(string label, UnityAction action)
        {
            GameObject buttonObject = GameObject.Instantiate(_buttonPrefab);
            buttonObject.transform.SetParent(_buttonsContainer.transform, false);
            buttonObject.transform.localScale = Vector3.one;

            TMP_Text buttonText = buttonObject.transform.GetChild(0).gameObject.GetComponent<TMP_Text>();
            // The inherited button prefab uses a decorative TMP asset whose O/0 glyph has very
            // different metrics from K, making short labels such as "OK" look like "0K". Dialogue
            // body text already uses the intended UI font, so reuse that asset/material while
            // preserving the button prefab's authored size, alignment, and style.
            buttonText.font = _explanationText.font;
            buttonText.fontSharedMaterial = _explanationText.fontSharedMaterial;
            buttonText.text = label;

            Button button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(delegate ()
            {
                if (action != null && action.Method.Name == "DeleteCurrentSquad")
                {
                    UIAudioController.Instance?.PlayDeleteSquadSound();
                }
                else
                {
                    UIAudioController.Instance?.PlayButtonSound();
                }

                action();
                Hide();
            });
            // Dialogue buttons are cloned after the scene-loaded scans, so apply both sound and
            // visual ownership directly when each control is created.
            ButtonSoundOwnershipGuard.Configure(button);
            GameHudLayoutGuard.ConfigureButtonStyle(button);
            buttonObject.SetActive(true);
            return buttonObject;
        }

        public void Show()
        {
            if (_playErrorSoundOnShow)
            {
                UIAudioController.Instance?.PlayErrorSound();
            }
            else if (_playSaveSoundOnShow)
            {
                UIAudioController.Instance?.PlaySaveSound();
            }
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
            GameObject previousButton = _buttons[index];
            int siblingIndex = previousButton.transform.GetSiblingIndex();

            // Destroy is deferred until the end of the frame. Hide the old button immediately so
            // a dialogue shown in this same frame never renders both the old and replacement controls.
            previousButton.SetActive(false);
            GameObject replacementButton = MakeButton(label, action);
            replacementButton.transform.SetSiblingIndex(siblingIndex);
            _buttons[index] = replacementButton;
            GameObject.Destroy(previousButton);
        }
    }
}
