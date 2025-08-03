using Assets.Scripts.Levels;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts
{
    public class AudioController : MonoBehaviour
    {
        public AudioSource PlutoIntro;
        public AudioSource NeptuneIntro;
        public AudioSource UranusIntro;

        public AudioSource PlutoLoop;
        public AudioSource NeptuneLoop;
        public AudioSource UranusLoop;

        public AudioSource PlutoHumanLoop;
        public AudioSource NeptuneHumanLoop;
        public AudioSource UranusHumanLoop;

        public AudioSource PlutoBeesLoop;
        public AudioSource NeptuneBeesLoop;
        public AudioSource UranusBeesLoop;

        public float PlutoIntroLength;
        public float NeptuneIntroLength;
        public float UranusIntroLength;

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
        public AudioSource BeesLoop;

        public AudioSource LightCannonSound;
        public AudioSource LightCannonSound2;
        public AudioSource SmallCannonSound;
        public AudioSource SmallCannonSound2;
        public AudioSource BigCannonSound;
        public AudioSource BigCannonSound2;
        public AudioSource BeamCannonSound;

        public List<AudioSource> TinyShipExplosionSounds;

        public AudioSource EnteringWarpGateSound;

        //public Dictionary<string, AudioSource> BeesIntros = new Dictionary<string, AudioSource>();
        //public Dictionary<string, AudioSource> BeesLoops = new Dictionary<string, AudioSource>();
        public Dictionary<ConfigData.WeaponSoundTypes, AudioSource[]> WeaponSounds = new Dictionary<ConfigData.WeaponSoundTypes, AudioSource[]>();
        public Dictionary<float, List<AudioSource>> ExplosionSounds = new Dictionary<float, List<AudioSource>>();
        public List<AudioSource> Loops = new List<AudioSource>();
        public List<AudioSource> Intros = new List<AudioSource>();

        public float IntroLength;
        public bool IntroEnded;
        public Level Level;

        public void Setup(bool playMusic, Level level)
        {
            Level = level;

            WeaponSounds.Add(ConfigData.WeaponSoundTypes.LightCannon, new AudioSource[] { LightCannonSound, LightCannonSound2 });
            WeaponSounds.Add(ConfigData.WeaponSoundTypes.SmallLaser, new AudioSource[] { SmallCannonSound, SmallCannonSound2 });
            WeaponSounds.Add(ConfigData.WeaponSoundTypes.BigLaser, new AudioSource[] { BigCannonSound, BigCannonSound2 });
            WeaponSounds.Add(ConfigData.WeaponSoundTypes.BeamCannon, new AudioSource[] { BeamCannonSound });

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



        }
        public void SetupMusic()
        {
            IntroEnded = false;
            Intros.ForEach((source) =>
            {
                source.Stop();
            });
            Loops.ForEach((source) =>
            {
                source.Stop();
            });
            Intros.Clear();
            Loops.Clear();


            switch (Level.MapData.Location)
            {
                case ConfigData.Locations.Pluto:
                    LocationIntro = PlutoIntro;
                    LocationLoop = PlutoLoop;
                    HumanLoop = PlutoHumanLoop;
                    BeesLoop = PlutoBeesLoop;
                    IntroLength = PlutoIntroLength;
                    break;
                case ConfigData.Locations.Neptune:
                    LocationIntro = NeptuneIntro;
                    LocationLoop = NeptuneLoop;
                    HumanLoop = NeptuneHumanLoop;
                    BeesLoop = NeptuneBeesLoop;
                    IntroLength = NeptuneIntroLength;
                    break;
                case ConfigData.Locations.Uranus:
                    LocationIntro = UranusIntro;
                    LocationLoop = UranusLoop;
                    HumanLoop = UranusHumanLoop;
                    BeesLoop = UranusBeesLoop;
                    IntroLength = UranusIntroLength;
                    break;
            }

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
            Loops.Add(BeesLoop);

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
            PlayLoop(IntroLength, BeesLoop);

            //StartCoroutine(nameof(EndIntro), IntroLength);
            //Invoke(nameof(EndIntro), IntroLength);
            Level.AddTimer(new ScaledTimer(IntroLength, EndIntro));
        }

        private void EndIntro()
        {
            //Debug.Log($"The intro has ended");
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
                // The intro has ended, the loops need to be paused
                Loops.ForEach((source) =>
                {
                    source.Pause();
                });
            }
            else
            {
                /// The intro has not ended, the loops need to be stopped and the intros need to be paused
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
                // The intro has ended, play the loops
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