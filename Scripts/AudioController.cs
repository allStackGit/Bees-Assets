using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts
{
    public class AudioController : MonoBehaviour
    {
        public AudioSource LocationIntro;
        public AudioSource CarpenterBeeIntro;
        public AudioSource HoneybeeIntro;
        public AudioSource HornetIntro;
        public AudioSource WaspIntro;

        public AudioSource LocationLoop;
        public AudioSource CarpenterBeeLoop;
        public AudioSource HoneybeeLoop;
        public AudioSource HornetLoop;
        public AudioSource WaspLoop;
        public AudioSource HumanLoop;

        public AudioSource LightCannonSound;  

        public Dictionary<string, AudioSource> BeesIntros = new Dictionary<string, AudioSource>();
        public Dictionary<string, AudioSource> BeesLoops = new Dictionary<string, AudioSource>();
        public Dictionary<string, AudioSource> WeaponSounds = new Dictionary<string, AudioSource>();
        public List<AudioSource> Loops = new List<AudioSource>();
        public List<AudioSource> Intros = new List<AudioSource>();

        public float IntroLength;
        public bool IntroEnded;

        public void Setup()
        {
            // Setup audio [make audio controller]
            BeesLoops.Add("Carpenter Bee", CarpenterBeeLoop);
            BeesLoops.Add("Honeybee", HoneybeeLoop);
            BeesLoops.Add("Hornet", HornetLoop);
            BeesLoops.Add("Wasp", WaspLoop);

            BeesIntros.Add("Carpenter Bee", CarpenterBeeIntro);
            BeesIntros.Add("Honeybee", HoneybeeIntro);
            BeesIntros.Add("Hornet", HornetIntro);
            BeesIntros.Add("Wasp", WaspIntro);

            Intros.Add(LocationIntro);

            Intros.Add(CarpenterBeeIntro);
            Intros.Add(HoneybeeIntro);
            Intros.Add(HornetIntro);
            Intros.Add(WaspIntro);

            Loops.Add(LocationLoop);
            Loops.Add(CarpenterBeeLoop);
            Loops.Add(HoneybeeLoop);
            Loops.Add(HornetLoop);
            Loops.Add(WaspLoop);
            Loops.Add(HumanLoop);

            WeaponSounds.Add("Light Cannon", LightCannonSound);

            //mute bee intros
            MuteSource(CarpenterBeeIntro);
            MuteSource(HoneybeeIntro);
            MuteSource(HornetIntro);
            MuteSource(WaspIntro);

            // play intros
            PlayIntro(LocationIntro);
            PlayIntro(CarpenterBeeIntro);
            PlayIntro(HoneybeeIntro);
            PlayIntro(HornetIntro);
            PlayIntro(WaspIntro);


            // mute bee loops
            MuteSource(CarpenterBeeLoop);
            MuteSource(HoneybeeLoop);
            MuteSource(HornetLoop);
            MuteSource(WaspLoop);

            // play loops
            PlayLoop(IntroLength, LocationLoop);
            PlayLoop(IntroLength, CarpenterBeeLoop);
            PlayLoop(IntroLength, HoneybeeLoop);
            PlayLoop(IntroLength, HornetLoop);
            PlayLoop(IntroLength, WaspLoop);
            PlayLoop(IntroLength, HumanLoop);
            //StartCoroutine(nameof(EndIntro), IntroLength);
            Invoke(nameof(EndIntro), IntroLength);

        }

        private void EndIntro()
        {
            IntroEnded = true;
        }
        private void PlayLoop(float delay, AudioSource loop)
        {
            if (loop != null)
            {
                loop.PlayDelayed(delay);
            }
        }
        private void PlayIntro(AudioSource intro)
        {
            if (intro != null)
            {
                intro.Play();
            }
        }
        public void MuteSource(AudioSource source)
        {
            if (source != null)
            {
                source.mute = true;
            }
        }
        public void Pause()
        {
            
            if (IntroEnded)
            {
                Loops.ForEach((source) =>
                {
                    source.Pause();
                });
            }
            else
            {
                Loops.ForEach((source) =>
                {
                    source.Stop();
                });
                Intros.ForEach((source) =>
                {
                    source.Pause();
                });
            }

        }
        public void Play()
        {
            if (IntroEnded)
            {
                Loops.ForEach((source) =>
                {
                    source.Play();
                });
            }
            else
            {
                float timeLeft = IntroLength;
                Intros.ForEach((source) =>
                {
                    source.Play();
                    timeLeft = IntroLength - source.time;
                });

                //Debug.Log($"Setting the loops to play after being paused. They were delayed by {IntroLength}s initially but they are now delayed by {timeLeft}s.");
                // play loops
                Loops.ForEach((source) =>
                {
                    PlayLoop(timeLeft, source);

                });
            }
        }
        public void UnMuteSource(AudioSource source)
        {
            if (source != null)
            {
                source.mute = false;
            }
        }
    }
}