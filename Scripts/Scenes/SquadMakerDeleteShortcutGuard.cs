using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.Scenes
{
    public sealed class SquadMakerDeleteShortcutGuard : MonoBehaviour
    {
        private SquadMaker _squadMaker;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            AttachToCurrentSquadMaker();
        }

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            AttachToCurrentSquadMaker();
        }

        private static void AttachToCurrentSquadMaker()
        {
            SquadMaker squadMaker = Object.FindObjectOfType<SquadMaker>();
            if (squadMaker == null || squadMaker.GetComponent<SquadMakerDeleteShortcutGuard>() != null)
            {
                return;
            }

            SquadMakerDeleteShortcutGuard guard = squadMaker.gameObject.AddComponent<SquadMakerDeleteShortcutGuard>();
            guard._squadMaker = squadMaker;
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                _squadMaker = GetComponent<SquadMaker>();
            }
        }

        private void Update()
        {
            if (_squadMaker == null || !Input.GetKeyDown(KeyCode.Delete) || IsEditingText())
            {
                return;
            }

            _squadMaker.ConfirmDeleteSquad();
        }

        private static bool IsEditingText()
        {
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null)
            {
                return false;
            }

            GameObject selected = EventSystem.current.currentSelectedGameObject;
            return selected.GetComponentInParent<TMP_InputField>() != null ||
                   selected.GetComponentInParent<InputField>() != null;
        }
    }
}
