using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Hurt sounds for the LOCAL player, from the user's own WAV files:
    ///   BepInEx/plugins/ER2AdvancedMedic/sounds/hitLight1..3.wav, hitMedium1..3.wav, hitHeavy1..3.wav
    /// Any of the nine may be missing. A hit picks its tier from what it did to you (WoundLogic.HurtTier)
    /// and plays a random clip of that tier - or of the nearest tier that has any - on our own 2D
    /// AudioSource. No files = silence and a single verbose-only log line.
    ///
    /// Files are found and decoded ONCE, at plugin load, on worker threads (pure managed byte work);
    /// only building the AudioClip needs the main thread, and <see cref="Pump"/> does one per frame.
    /// Nothing is loaded or decoded on a hit.
    /// </summary>
    internal static class HitSounds
    {
        public enum Tier { Light = 0, Medium = 1, Heavy = 2 }

        private static readonly string[] Prefix = { "hitLight", "hitMedium", "hitHeavy" };
        private const int PerTier = 3;

        private static readonly List<AudioClip>[] _clips = { new List<AudioClip>(), new List<AudioClip>(), new List<AudioClip>() };

        // Nearest tier with clips when the hit's own tier has none.
        private static readonly int[][] Fallback = { new[] { 0, 1, 2 }, new[] { 1, 0, 2 }, new[] { 2, 1, 0 } };

        private sealed class Decoded
        {
            public int Tier;
            public string File, Error;
            public float[] Samples;
            public int Channels, Rate;
            public float Peak;
        }

        private static readonly ConcurrentQueue<Decoded> _ready = new ConcurrentQueue<Decoded>();

        private static AudioSource _src;
        private static bool _failed;
        private static float _lastPlay = -999f;

        /// <summary>The sounds folder next to our dll.</summary>
        public static string Folder()
        {
            var dir = Paths.PluginDir();
            return dir == null ? null : Path.Combine(dir, "sounds");
        }

        /// <summary>Plugin load: make sure the folder exists (so users can see where the files go), then
        /// start decoding whatever is in it.</summary>
        public static void Init()
        {
            try
            {
                var dir = Folder();
                if (dir == null) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                WriteReadme(dir);

                int found = 0;
                for (int t = 0; t < Prefix.Length; t++)
                    for (int i = 1; i <= PerTier; i++)
                    {
                        string path = Path.Combine(dir, Prefix[t] + i + ".wav");
                        if (!File.Exists(path)) continue;
                        found++;
                        int tier = t;
                        Task.Run(() =>
                        {
                            var d = new Decoded { Tier = tier, File = path };
                            try { Decode(path, d); }
                            catch (Exception e) { d.Error = e.Message; d.Samples = null; }
                            _ready.Enqueue(d);
                        });
                    }

                Plugin.L.LogInfo($"[sound] hit sounds: {found} file(s) found in {dir}");
            }
            catch (Exception e) { Plugin.L.LogInfo("[sound] hit sounds unavailable: " + e.Message); }
        }

        private static void WriteReadme(string dir)
        {
            try
            {
                var p = Path.Combine(dir, "README.txt");
                if (File.Exists(p)) return;
                File.WriteAllText(p,
                    "Advanced Medic hurt sounds (optional).\r\n" +
                    "Put WAV files here named hitLight1.wav..hitLight3.wav, hitMedium1.wav..hitMedium3.wav and\r\n" +
                    "hitHeavy1.wav..hitHeavy3.wav. Any of them may be missing. PCM 8/16/24/32-bit or 32/64-bit float.\r\n" +
                    "Light = minor wounds / bruises, Medium = a medium wound or shrapnel, Heavy = a large wound,\r\n" +
                    "a broken bone, or a hit that downs you. Config: [HitSounds] in the Advanced Medic .cfg.\r\n" +
                    "New files are picked up on the next game start.\r\n");
            }
            catch { }
        }

        /// <summary>Main thread, once per frame: turn at most one decoded file into an AudioClip.</summary>
        public static void Pump()
        {
            if (_ready.IsEmpty) return;
            if (!_ready.TryDequeue(out var d)) return;
            string name = Path.GetFileName(d.File);
            if (d.Error != null || d.Samples == null || d.Channels <= 0)
            {
                Plugin.L.LogWarning($"[sound] {name} not loaded: {d.Error ?? "no samples"}");
                return;
            }
            try
            {
                int frames = d.Samples.Length / d.Channels;
                if (frames <= 0) { Plugin.L.LogWarning($"[sound] {name} holds no audio"); return; }
                var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(d.File), frames, d.Channels, d.Rate, false);
                clip.SetData(new Il2CppStructArray<float>(d.Samples), 0);
                clip.hideFlags = HideFlags.HideAndDontSave;   // survives scene changes
                _clips[d.Tier].Add(clip);
                Plugin.L.LogInfo($"[sound] loaded {name} ({clip.length:0.00}s, {d.Channels}ch, {d.Rate}Hz, peak {d.Peak:0.000})");
                if (d.Peak < 0.001f) Plugin.L.LogWarning($"[sound] {name} is silent");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[sound] {name} not loaded: {e.Message}"); }
        }

        /// <summary>Play a random hurt sound of this tier (or the nearest one that has files).</summary>
        public static void Play(Tier tier)
        {
            var c = Plugin.C;
            if (!c.HitSounds.Value || _failed) return;
            float now = Time.unscaledTime;
            if (now - _lastPlay < Mathf.Max(0f, c.HitSoundMinInterval.Value)) return;

            List<AudioClip> list = null;
            foreach (int t in Fallback[(int)tier])
                if (_clips[t].Count > 0) { list = _clips[t]; break; }
            if (list == null) return;   // no files at all: silence

            var clip = list[UnityEngine.Random.Range(0, list.Count)];
            if (clip == null || !Ensure()) return;
            try
            {
                _src.PlayOneShot(clip, Mathf.Clamp01(c.HitSoundVolume.Value));
                _lastPlay = now;
            }
            catch (Exception e) { Fail(e); }
        }

        private static bool Ensure()
        {
            if (_src != null) return true;
            try
            {
                var go = new GameObject("AdvancedMedic_HitSounds");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _src = go.AddComponent<AudioSource>();
                _src.spatialBlend = 0f;   // 2D: it's your own voice
                _src.playOnAwake = false;
                _src.priority = 40;
                return _src != null;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        private static void Fail(Exception e)
        {
            _failed = true;
            Plugin.L.LogWarning("[sound] hit sounds disabled: " + e.Message);
        }

        // ---- WAV (RIFF) decoder. Thread-safe: touches no Unity API. ----------------------------------
        // Uncompressed PCM 8 (unsigned) / 16 / 24 / 32-bit, IEEE float 32 / 64-bit, and
        // WAVE_FORMAT_EXTENSIBLE (0xFFFE), whose real format is the first two bytes of its SubFormat
        // GUID. Samples are decoded by CONTAINER size, so a 24-in-32 extensible file is right too.

        private const int FmtPcm = 1, FmtFloat = 3, FmtExtensible = 0xFFFE;

        private static void Decode(string path, Decoded r)
        {
            byte[] d = File.ReadAllBytes(path);

            if (d.Length < 12 ||
                Encoding.ASCII.GetString(d, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(d, 8, 4) != "WAVE")
                throw new Exception("not a RIFF/WAVE file");

            int format = 0, channels = 0, rate = 0, bits = 0;
            int dataOffset = -1, dataLength = 0;

            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                string id = Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                int body = pos + 8;

                if (id == "fmt " && body + 16 <= d.Length)
                {
                    format = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    bits = BitConverter.ToUInt16(d, body + 14);
                    // cbSize(2) validBits(2) channelMask(4) then the SubFormat GUID at +24.
                    if (format == FmtExtensible && size >= 40 && body + 26 <= d.Length)
                        format = BitConverter.ToUInt16(d, body + 24);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    // A streamed/truncated file can declare more (or 0xFFFFFFFF): take what is there.
                    dataLength = size < 0 ? d.Length - body : Math.Min(size, d.Length - body);
                }

                if (size < 0 || body + size > d.Length) break;
                pos = body + size + (size & 1);   // chunks are word-aligned
            }

            if (dataOffset < 0 || channels <= 0 || rate <= 0)
                throw new Exception("missing fmt or data chunk");
            if (format != FmtPcm && format != FmtFloat)
                throw new Exception($"compressed WAV (format {format}) is not supported - export as PCM or float");

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) throw new Exception("bad bit depth");
            int total = dataLength / bytesPerSample;
            total -= total % channels;   // whole frames only
            var s = new float[total];

            if (format == FmtFloat)
            {
                if (bits == 32)      for (int i = 0; i < total; i++) s[i] = BitConverter.ToSingle(d, dataOffset + i * 4);
                else if (bits == 64) for (int i = 0; i < total; i++) s[i] = (float)BitConverter.ToDouble(d, dataOffset + i * 8);
                else throw new Exception($"unsupported float bit depth {bits}");
            }
            else
            {
                switch (bits)
                {
                    case 8:    // unsigned
                        for (int i = 0; i < total; i++) s[i] = (d[dataOffset + i] - 128) / 128f;
                        break;
                    case 16:
                        for (int i = 0; i < total; i++) s[i] = BitConverter.ToInt16(d, dataOffset + i * 2) / 32768f;
                        break;
                    case 24:
                        for (int i = 0; i < total; i++)
                        {
                            int o = dataOffset + i * 3;
                            int v = (d[o] << 8) | (d[o + 1] << 16) | (d[o + 2] << 24);   // sign-extend via the top byte
                            s[i] = (v >> 8) / 8388608f;
                        }
                        break;
                    case 32:
                        for (int i = 0; i < total; i++) s[i] = BitConverter.ToInt32(d, dataOffset + i * 4) / 2147483648f;
                        break;
                    default:
                        throw new Exception($"unsupported bit depth {bits}");
                }
            }

            float peak = 0f;
            for (int i = 0; i < total; i++) { float a = s[i] < 0f ? -s[i] : s[i]; if (a > peak) peak = a; }

            r.Samples = s;
            r.Channels = channels;
            r.Rate = rate;
            r.Peak = peak;
        }
    }
}
