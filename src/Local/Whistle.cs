using System;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FlippingIsTag.Local
{
    /// <summary>
    /// The taunt whistle, played in the world where the hider is, so the seekers can follow it
    /// by ear.
    ///
    /// The game's own sounds go through FMOD, but Unity's audio is left on (voice chat uses it,
    /// through the AudioListener on CameraManager's voice anchor), so a plain AudioSource is
    /// heard. The sound ships inside the dll as a 16-bit WAV: Unity only makes a clip from
    /// samples at runtime, and WAV is the one format whose samples need no decoder.
    /// </summary>
    internal static class Whistle
    {
        private const string Resource = "FlippingIsTag.whistle.wav";

        /// <summary>Full volume up to this many metres.</summary>
        private const float NearDistance = 4f;

        /// <summary>Silent from this many metres on. Linear in between, so it carries across a level.</summary>
        private const float FarDistance = 70f;

        private static AudioClip clip;
        private static bool failed;
        private static bool listenerChecked;

        public static void PlayAt(Vector3 position)
        {
            AudioClip sound = Load();

            if (sound == null)
            {
                return;
            }

            CheckListener();

            var go = new GameObject("FlippingIsTag_Whistle");
            go.transform.position = position;

            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = sound;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = NearDistance;
            source.maxDistance = FarDistance;
            source.dopplerLevel = 0f;
            source.Play();

            Object.Destroy(go, sound.length + 0.2f);
        }

        private static AudioClip Load()
        {
            if (clip != null || failed)
            {
                return clip;
            }

            try
            {
                using Stream stream = typeof(Whistle).Assembly.GetManifestResourceStream(Resource)
                                      ?? throw new InvalidOperationException($"{Resource} is not in the dll");
                using var reader = new BinaryReader(stream);

                float[] samples = ReadWav(reader, out int channels, out int rate);

                clip = AudioClip.Create("FlippingIsTag_Whistle", samples.Length / channels, channels, rate, false);

                // Kept across levels: a scene change unloads assets nothing in the scene holds.
                clip.hideFlags = HideFlags.HideAndDontSave;
                clip.SetData(samples, 0);
            }
            catch (Exception ex)
            {
                failed = true;
                clip = null;
                TagPlugin.Logger.LogError($"Could not load the whistle; taunts will be silent: {ex.Message}");
            }

            return clip;
        }

        /// <summary>16-bit PCM only: the file is made for the mod, so anything else is a build mistake.</summary>
        private static float[] ReadWav(BinaryReader reader, out int channels, out int rate)
        {
            if (Tag(reader) != "RIFF")
            {
                throw new InvalidDataException("not a RIFF file");
            }

            reader.ReadInt32();

            if (Tag(reader) != "WAVE")
            {
                throw new InvalidDataException("not a WAVE file");
            }

            channels = 0;
            rate = 0;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string chunk = Tag(reader);
                int size = reader.ReadInt32();

                if (chunk == "fmt ")
                {
                    int format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    int bits = reader.ReadInt16();
                    reader.BaseStream.Seek(size - 16, SeekOrigin.Current);

                    if (format != 1 || bits != 16 || channels < 1)
                    {
                        throw new InvalidDataException($"format {format}, {bits} bits, {channels} channels; expected 16-bit PCM");
                    }
                }
                else if (chunk == "data")
                {
                    if (channels == 0)
                    {
                        throw new InvalidDataException("data before fmt");
                    }

                    var samples = new float[size / 2];

                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = reader.ReadInt16() / 32768f;
                    }

                    return samples;
                }
                else
                {
                    // Chunks are padded to an even size.
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }

            throw new InvalidDataException("no data chunk");
        }

        private static string Tag(BinaryReader reader) => new(reader.ReadChars(4));

        /// <summary>A whistle nobody can hear is a bug worth a line in the log, once.</summary>
        private static void CheckListener()
        {
            if (listenerChecked)
            {
                return;
            }

            listenerChecked = true;

            if (Object.FindObjectsOfType<AudioListener>().Length == 0)
            {
                TagPlugin.Logger.LogWarning("No AudioListener in the scene: the whistle plays but nobody hears it.");
            }
        }
    }
}
