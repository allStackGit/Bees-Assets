using Assets.Scripts;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class SettingsMenu : MonoBehaviour
{
    public GameObject ControlsList, Entry;
    public List<HotKey> HotKeys;
    public Stage Stage;

    private HotKey _currentHotKey;
    private TMP_InputField _currentEntry;
    private bool _hasKeyInputEnabled;
    private bool _isSetup;
    private bool _hasChangedHotKeys;
    private readonly List<KeyCode> _newKeyCombination = new List<KeyCode>();
    private readonly List<TMP_InputField> _entryInputs = new List<TMP_InputField>();
    private readonly Dictionary<TMP_InputField, HotKey> _hotKeysByEntry = new Dictionary<TMP_InputField, HotKey>();

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
                    _newKeyCombination.Add(key);
                }
            }
        }
        else if (_newKeyCombination.Count > 0)
        {
            _currentHotKey.SetKeyCombination(_newKeyCombination.ToList());
            _currentEntry.text = _currentHotKey.KeyString;
            _hasChangedHotKeys = true;
            ConfigData.GetUserSettingsData().SetKey(_currentHotKey.Name, _currentHotKey.Keys);
            _newKeyCombination.Clear();
            _hasKeyInputEnabled = false;
        }
    }

    public void ViewSettings()
    {
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
        return _entryInputs.FirstOrDefault(e => e.text == keyString);
    }

    public void SetupSettings(Stage stage)
    {
        Stage = stage;
        HotKeys = ConfigData.GetUserSettingsData().HotKeys;
        _entryInputs.Clear();
        _hotKeysByEntry.Clear();

        HotKeys.ForEach(key =>
        {
            Transform list = ControlsList.transform.GetChild(0).GetChild(0);
            GameObject entry = Instantiate(Entry);
            entry.transform.SetParent(list);
            entry.transform.localScale = Vector3.one;

            TMP_Text actionName = entry.transform.GetChild(0).GetChild(1).GetChild(0).GetComponent<TMP_Text>();
            TMP_InputField keyInput = entry.transform.GetChild(1).GetComponent<TMP_InputField>();

            actionName.text = key.Name;
            keyInput.text = key.KeyString;

            entry.SetActive(true);
            _entryInputs.Add(keyInput);
            _hotKeysByEntry[keyInput] = key;
        });
        _isSetup = true;
    }

    public void SelectTextInput(string selection)
    {
        // The serialized TMP_InputField OnSelect event passes the displayed binding string,
        // but bindings are not unique. Resolve the actual selected input object instead.
        TMP_InputField selectedEntry = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.GetComponent<TMP_InputField>();
        if (selectedEntry != null && _hotKeysByEntry.TryGetValue(selectedEntry, out HotKey selectedHotKey))
        {
            _currentEntry = selectedEntry;
            _currentHotKey = selectedHotKey;
            _hasKeyInputEnabled = true;
            return;
        }

        // Fallback for programmatic callers that do not select through the EventSystem.
        _currentEntry = FindEntryByKeyString(selection);
        if (_currentEntry != null && _hotKeysByEntry.TryGetValue(_currentEntry, out selectedHotKey))
        {
            _currentHotKey = selectedHotKey;
            _hasKeyInputEnabled = true;
        }
    }
}
