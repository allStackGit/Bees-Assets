using Assets.Scripts;
using Assets.Scripts.Level;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.Text;

public class SettingsMenu : MonoBehaviour
{
    public GameObject ControlsList, Entry;
    public List<HotKey> HotKeys;
    private readonly Dictionary<char, KeyCode> _keycodeCache = new Dictionary<char, KeyCode>();
    private HotKey _currentHotKey;
    public void ViewSettings()
    {
        Debug.Log("Viewing settings");
        gameObject.SetActive(true);
    }
    public void ExitSettings()
    {
        gameObject.SetActive(false);
    }
    public void ViewControls()
    {
        ControlsList.SetActive(true);
    }
    public void SetupSettings()
    {
        HotKeys = ConfigData.GetUserSettingsData().HotKeys;
        HotKeys.ForEach(key =>
        {
            Transform list = ControlsList.transform.GetChild(0).GetChild(0);
            GameObject entry = Instantiate(Entry);
            entry.transform.parent = list;
            entry.transform.localScale = Vector3.one;


            TMP_Text actionName = entry.transform.GetChild(0).GetChild(1).GetChild(0).GetComponent<TMP_Text>();
            TMP_InputField keyInput = entry.transform.GetChild(1).GetComponent<TMP_InputField>();

            actionName.text = key.Name;
            keyInput.text = key.KeyString;

            entry.gameObject.SetActive(true);
        });
    }
    public void ValidateKeyInput(string key)
    {
        Debug.Log($"key pressed: {key}, keycode: {GetKeyCode(key.ToCharArray()[0])}");
    }
    public void SelectTextInput(string selection)
    {
        Debug.Log($"Text input selected: {selection}");
        _currentHotKey = ConfigData.GetUserSettingsData().FindKeyByKeyString(selection);
        Debug.Log(_currentHotKey);
    }

    public KeyCode GetKeyCode(char key)
    {
        KeyCode keyCode;
        if (_keycodeCache.ContainsKey(key) )
        {
            keyCode = _keycodeCache[key];
        }
        else
        {
            int keyNumber = key;
            keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), keyNumber.ToString());
            _keycodeCache.Add(key, keyCode);

        }
        return keyCode;
    }

}
