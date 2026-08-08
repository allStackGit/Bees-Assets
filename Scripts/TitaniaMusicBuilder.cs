using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// Builds Titania's one-shot intro and seamless looping body from the
    /// full authored source track. The source keeps its Unity-generated
    /// importer metadata; no generated audio .meta files are required.
    /// </summary>
    public static class TitaniaMusicBuilder
    {
        public const float IntroEndSeconds = 26.565215f;
        public const float LoopEndSeconds = 185.461179f;
        public const string ResourcePath = "Music/Titania/Titania Source";

        private static AudioClip _intro;
        private static AudioClip _loop;

        public static bool TryGetClips(out AudioClip intro, out AudioClip loop)
        {
            if (_intro != null && _loop != null)
            {
                intro = _intro;
                loop = _loop;
                return true;
            }

            AudioClip source = Resources.Load<AudioClip>(ResourcePath);
            if (source == null)
            {
                Debug.LogError($"Missing Titania music source at Resources/{ResourcePath}");
                intro = null;
                loop = null;
                return false;
            }

            int introEnd = Mathf.Clamp(Mathf.RoundToInt(IntroEndSeconds * source.frequency), 1, source.samples);
            int loopEnd = Mathf.Clamp(Mathf.RoundToInt(LoopEndSeconds * source.frequency), introEnd + 1, source.samples);

            _intro = CopySegment(source, 0, introEnd, "Titania Intro");
            _loop = CopySegment(source, introEnd, loopEnd, "Titania Loop");
            Resources.UnloadAsset(source);

            intro = _intro;
            loop = _loop;
            return true;
        }

        private static AudioClip CopySegment(AudioClip source, int startFrame, int endFrame, string name)
        {
            int frameCount = endFrame - startFrame;
            AudioClip result = AudioClip.Create(name, frameCount, source.channels, source.frequency, false);

            // Copy in short chunks so splitting a long stereo soundtrack does
            // not allocate one enormous temporary float array.
            int copiedFrames = 0;
            int maxChunkFrames = Mathf.Max(1, source.frequency * 2);
            while (copiedFrames < frameCount)
            {
                int chunkFrames = Mathf.Min(maxChunkFrames, frameCount - copiedFrames);
                float[] data = new float[chunkFrames * source.channels];
                if (!source.GetData(data, startFrame + copiedFrames))
                {
                    Object.Destroy(result);
                    throw new System.InvalidOperationException($"Could not read {name} from the Titania source clip.");
                }
                if (!result.SetData(data, copiedFrames))
                {
                    Object.Destroy(result);
                    throw new System.InvalidOperationException($"Could not build runtime clip {name}.");
                }
                copiedFrames += chunkFrames;
            }

            return result;
        }
    }
}
