using Assets.Scripts.Levels;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts
{
    public class AudioController : MonoBehaviour
    {
        public AudioSource LocationIntro;
        //public AudioSource CarpenterBeeIntro;
        //public AudioSource HoneybeeIntro;
        //public AudioSource HornetIntro;
        //public AudioSource WaspIntro;

        public AudioSource LocationLoop;
        //public AudioSource CarpenterBeeLoop;
        //public AudioSource HoneybeeLoop;
        //public AudioSource HornetLoop;
        //public AudioSource WaspLoop;
        public AudioSource HumanLoop;

        public AudioSource LightCannonSound;
        public AudioSource LightCannonSound2;
        public AudioSource SmallCannonSound;
        public AudioSource SmallCannonSound2;
        public AudioSource BigCannonSound;
        public AudioSource BigCannonSound2;

        public List<AudioSource> TinyShipExplosionSounds;

        //public Dictionary<string, AudioSource> BeesIntros = new Dictionary<string, AudioSource>();
        //public Dictionary<string, AudioSource> BeesLoops = new Dictionary<string, AudioSource>();
        public Dictionary<ConfigData.WeaponTypes, AudioSource[]> WeaponSounds = new Dictionary<ConfigData.WeaponTypes, AudioSource[]>();
        public Dictionary<float, List<AudioSource>> ExplosionSounds = new Dictionary<float, List<AudioSource>>();
        public List<AudioSource> Loops = new List<AudioSource>();
        public List<AudioSource> Intros = new List<AudioSource>();

        public float IntroLength;
        public bool IntroEnded;
        public Level Level;

        public void Setup(bool playMusic, Level level)
        {
            Level = level;
            // Setup audio [make audio controller]
            //BeesLoops.Add("Carpenter Bee", CarpenterBeeLoop);
            //BeesLoops.Add("Honeybee", HoneybeeLoop);
            //BeesLoops.Add("Hornet", HornetLoop);
            //BeesLoops.Add("Wasp", WaspLoop);

            //BeesIntros.Add("Carpenter Bee", CarpenterBeeIntro);
            //BeesIntros.Add("Honeybee", HoneybeeIntro);
            //BeesIntros.Add("Hornet", HornetIntro);
            //BeesIntros.Add("Wasp", WaspIntro);

            Intros.Add(LocationIntro);

            //Intros.Add(CarpenterBeeIntro);
            //Intros.Add(HoneybeeIntro);
            //Intros.Add(HornetIntro);
            //Intros.Add(WaspIntro);

            Loops.Add(LocationLoop);
            //Loops.Add(CarpenterBeeLoop);
            //Loops.Add(HoneybeeLoop);
            //Loops.Add(HornetLoop);
            //Loops.Add(WaspLoop);
            Loops.Add(HumanLoop);

            WeaponSounds.Add(ConfigData.WeaponTypes.LightCannon, new AudioSource[] { LightCannonSound, LightCannonSound2 });
            WeaponSounds.Add(ConfigData.WeaponTypes.Turret, new AudioSource[] { SmallCannonSound, SmallCannonSound2 });
            WeaponSounds.Add(ConfigData.WeaponTypes.FullShipTurret, new AudioSource[] { BigCannonSound, BigCannonSound2 });

            ExplosionSounds.Add(ConfigData.Tiny, TinyShipExplosionSounds);

            //mute bee intros
            //MuteSource(CarpenterBeeIntro);
            //MuteSource(HoneybeeIntro);
            //MuteSource(HornetIntro);
            //MuteSource(WaspIntro);




            // mute bee loops
            //MuteSource(CarpenterBeeLoop);
            //MuteSource(HoneybeeLoop);
            //MuteSource(HornetLoop);
            //MuteSource(WaspLoop);

            if (playMusic)
            {
                // play intros
                PlayIntro(LocationIntro);
                //PlayIntro(CarpenterBeeIntro);
                //PlayIntro(HoneybeeIntro);
                //PlayIntro(HornetIntro);
                //PlayIntro(WaspIntro);

                // play loops
                PlayLoop(IntroLength, LocationLoop);
                //PlayLoop(IntroLength, CarpenterBeeLoop);
                //PlayLoop(IntroLength, HoneybeeLoop);
                //PlayLoop(IntroLength, HornetLoop);
                //PlayLoop(IntroLength, WaspLoop);
                PlayLoop(IntroLength, HumanLoop);

                //StartCoroutine(nameof(EndIntro), IntroLength);
                //Invoke(nameof(EndIntro), IntroLength);
                Level.AddTimer(new ScaledTimer(IntroLength, EndIntro));
            }



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