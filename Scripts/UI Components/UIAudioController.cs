using System.Collections;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    public class UIAudioController : MonoBehaviour
    {
        public static UIAudioController Instance { get; private set; }
        public AudioSource ButtonClick;
        public AudioSource MenuMusic;

        void Awake()
        {
            // Implement singleton pattern
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                // Destroy duplicate instances
                Destroy(gameObject);
            }
        }
        public void PlayButtonSound()
        {
            ButtonClick.Play();
        }
        public void PlayMusic()
        {
            if (!MenuMusic.isPlaying)
            {
                MenuMusic.Play();
            }
        }
        public void PauseMusic()
        {
            //Debug.Log($"Stopping the menu music");
            MenuMusic.Pause();
        }
    }
}