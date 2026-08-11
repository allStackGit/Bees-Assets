using UnityEngine;
using Assets.Scripts.Levels;

namespace Assets.Scripts.UI_Components
{
    public class UIAudioController : MonoBehaviour
    {
        public static UIAudioController Instance { get; private set; }

        public AudioSource ButtonClick;
        public AudioSource MenuMusic;

        public AudioClip DeleteSquadSound;
        public AudioClip ErrorSound;
        public AudioClip EngineHumSound;
        public AudioClip IntercomNotificationSound;
        public AudioClip SaveSound;

        private const float ReducedNotificationVolume = 0.5625f;
        private AudioSource _levelIntroAmbience;

        void Awake()
        {
            if (CampaignScenarioIsolation.IsActive)
            {
                enabled = false;
                return;
            }

            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                SetupLevelIntroAmbience();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void SetupLevelIntroAmbience()
        {
            _levelIntroAmbience = gameObject.AddComponent<AudioSource>();
            _levelIntroAmbience.playOnAwake = false;
            _levelIntroAmbience.loop = true;
            _levelIntroAmbience.spatialBlend = 0f;
            _levelIntroAmbience.volume = 0.35f;

            if (MenuMusic != null)
            {
                _levelIntroAmbience.outputAudioMixerGroup = MenuMusic.outputAudioMixerGroup;
            }
        }

        private void PlayUiClip(AudioClip clip, float volumeScale = 1f)
        {
            if (ButtonClick != null && clip != null)
            {
                ButtonClick.PlayOneShot(clip, volumeScale);
            }
        }

        public void PlayButtonSound()
        {
            if (ButtonClick != null && ButtonClick.clip != null)
            {
                ButtonClick.PlayOneShot(ButtonClick.clip);
            }
        }

        public void PlayDeleteSquadSound()
        {
            PlayUiClip(DeleteSquadSound, ReducedNotificationVolume);
        }

        public void PlayErrorSound()
        {
            PlayUiClip(ErrorSound);
        }

        public void PlayIntercomSound()
        {
            PlayUiClip(IntercomNotificationSound, ReducedNotificationVolume);
        }

        public void PlaySaveSound()
        {
            PlayUiClip(SaveSound, ReducedNotificationVolume);
        }

        public void PlayLevelIntroAmbience()
        {
            if (_levelIntroAmbience == null)
            {
                SetupLevelIntroAmbience();
            }
            if (EngineHumSound == null || _levelIntroAmbience.isPlaying)
            {
                return;
            }

            _levelIntroAmbience.clip = EngineHumSound;
            _levelIntroAmbience.Play();
        }

        public void StopLevelIntroAmbience()
        {
            if (_levelIntroAmbience != null)
            {
                _levelIntroAmbience.Stop();
            }
        }

        public void PlayMusic()
        {
            if (MenuMusic != null && !MenuMusic.isPlaying)
            {
                MenuMusic.Play();
            }
        }

        public void PauseMusic()
        {
            if (MenuMusic != null)
            {
                MenuMusic.Pause();
            }
        }
    }
}
