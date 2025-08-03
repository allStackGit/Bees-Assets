using System.Collections;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    public class UIAudioController : MonoBehaviour
    {
        public static UIAudioController Instance { get; private set; }
        public AudioSource ButtonClick;

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
    }
}