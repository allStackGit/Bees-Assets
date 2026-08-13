using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    internal static class ButtonSoundOwnershipGuard
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Button button in root.GetComponentsInChildren<Button>(true))
                {
                    bool ownsSerializedButtonSound = false;
                    for (int index = 0; index < button.onClick.GetPersistentEventCount(); index++)
                    {
                        Object target = button.onClick.GetPersistentTarget(index);
                        string methodName = button.onClick.GetPersistentMethodName(index);
                        if (target is UIAudioController && methodName == nameof(UIAudioController.PlayButtonSound))
                        {
                            button.onClick.SetPersistentListenerState(index, UnityEventCallState.Off);
                            ownsSerializedButtonSound = true;
                        }
                    }

                    if (ownsSerializedButtonSound && button.GetComponent<ButtonSoundOwner>() == null)
                    {
                        button.gameObject.AddComponent<ButtonSoundOwner>();
                    }
                }
            }
        }
    }

    internal sealed class ButtonSoundOwner : MonoBehaviour, IPointerDownHandler
    {
        private Button _button;
        private bool _buttonSoundWasPlayingBeforeClick;

        private void Awake()
        {
            _button = GetComponent<Button>();
        }

        private void OnEnable()
        {
            if (_button == null)
            {
                _button = GetComponent<Button>();
            }
            _button?.onClick.AddListener(PlayButtonSoundIfActionDidNot);
        }

        private void OnDisable()
        {
            _button?.onClick.RemoveListener(PlayButtonSoundIfActionDidNot);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            AudioSource source = UIAudioController.Instance != null
                ? UIAudioController.Instance.ButtonClick
                : null;
            _buttonSoundWasPlayingBeforeClick = source != null && source.isPlaying;
        }

        private void PlayButtonSoundIfActionDidNot()
        {
            UIAudioController audio = UIAudioController.Instance;
            if (audio == null)
            {
                return;
            }

            AudioSource source = audio.ButtonClick;
            bool actionStartedUiSound = !_buttonSoundWasPlayingBeforeClick &&
                                        source != null && source.isPlaying;
            if (!actionStartedUiSound)
            {
                audio.PlayButtonSound();
            }

            _buttonSoundWasPlayingBeforeClick = source != null && source.isPlaying;
        }
    }
}
