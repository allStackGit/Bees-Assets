using Assets.Scripts;
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.Text;

public class SettingsMenu : MonoBehaviour
{
    public GameObject ControlsList, Entry;
    public List<HotKey> HotKeys;
    public Stage Stage;
    private readonly Dictionary<char, KeyCode> _keycodeCache = new Dictionary<char, KeyCode>();
    private HotKey _currentHotKey;
    private TMP_InputField _currentEntry;
    private bool _hasKeyInputEnabled;
    private bool _isSetup;
    private bool _hasChangedHotKeys;
    private List<KeyCode> _newKeyCombination = new List<KeyCode>();
    private List<TMP_InputField> _entryInputs = new List<TMP_InputField>();

    public void Update()
    {
        if (_isSetup && _hasKeyInputEnabled)
        {
            CheckForKeyInput();
        }
    }
    public void CheckForKeyInput()
    {
        if (Input.anyKey)
        {
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                if (key < KeyCode.Mouse0 && Input.GetKeyDown(key))
                {
                    //Debug.Log("Key pressed: " + key);
                    _newKeyCombination.Add(key);
                    //Debug.Log($"Current keys pressed: {Utilities.ListToString(_newKeyCombination)}");
                }
            }

        }
        else if (_newKeyCombination.Count > 0)
        {
            //Debug.Log($"All keys have been released");
            _currentHotKey.SetKeyCombination(_newKeyCombination.ToList());
            _currentEntry.text = _currentHotKey.KeyString;
            _hasChangedHotKeys = true;
            //Debug.Log($"Chosen hot key: {_currentHotKey}");
            ConfigData.GetUserSettingsData().SetKey(_currentHotKey.Name, _currentHotKey.Keys);
            _newKeyCombination.Clear();
            _hasKeyInputEnabled = false;
        }
    }
    public void ViewSettings()
    {
        //Debug.Log("Viewing settings");
        _hasChangedHotKeys = false;
        _newKeyCombination.Clear();
        _hasKeyInputEnabled = false;
        _currentEntry = null;
        _currentHotKey = null;
        gameObject.SetActive(true);
    }
    public void ExitSettings()
    {
        if (_hasChangedHotKeys)
        {
            //Debug.Log("Saving settings");
            ConfigData.GetUserSettingsData().Save();
            Stage.InputManager.LoadHotKeySettings();
        }
        gameObject.SetActive(false);
    }
    public void ViewControls()
    {
        ControlsList.SetActive(true);
    }
    public TMP_InputField FindEntryByKeyString(string keyString)
    {
        return _entryInputs.FirstOrDefault((e) => e.text == keyString);
    }
    public void SetupSettings(Stage stage)
    {
        Stage = stage;
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
            _entryInputs.Add(keyInput);
        });
        _isSetup = true;
    }
    public void SelectTextInput(string selection)
    {
        //Debug.Log($"Text input selected: {selection}");
        _currentHotKey = ConfigData.GetUserSettingsData().FindKeyByKeyString(selection);
        _currentEntry = FindEntryByKeyString(selection);
        if (_currentHotKey != null && _currentEntry != null)
        {
            _hasKeyInputEnabled = true;
        }
    }

}
