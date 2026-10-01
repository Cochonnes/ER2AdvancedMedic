using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Heartbeat: when the local player's heart races (above HeartbeatFastAbove) or crawls
    /// (below HeartbeatSlowBelow, but still beating), play a heartbeat paced at 60/HR seconds. The
    /// "lub-dub" is synthesised once at runtime (two decaying low sine thumps), so no sound files ship.
    /// The source and clip are HideAndDontSave so a scene change can't destroy them.
    /// </summary>
    internal static class Heartbeat
    {
        private static AudioSource _src;
        private static AudioClip _clip;
        private static float _next;
        private static bool _failed;

        public static void Reset() { _next = 0f; }

        public static void Tick(Soldier player)
        {
            var c = Plugin.C;
            if (!c.HeartbeatSound.Value || _failed || player == null) return;
            var w = WoundState.Peek(player);
            if (w == null || !w.VitalsInit) return;

            float hr = w.Pulse;
            if (hr <= 1f) return;
            if (hr < c.HeartbeatFastAbove.Value && hr > c.HeartbeatSlowBelow.Value) return;

            float now = Time.time;
            if (now < _next) return;
            _next = now + 60f / hr;

            if (!Ensure()) return;
            try { _src.PlayOneShot(_clip, Mathf.Clamp01(c.HeartbeatVolume.Value)); }
            catch (Exception e) { Fail(e); }
        }

        private static bool Ensure()
        {
            if (_src != null && _clip != null) return true;
            try
            {
                if (_clip == null) _clip = Synthesize();
                if (_src == null)
                {
                    var go = new GameObject("AdvancedMedic_Heartbeat");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _src = go.AddComponent<AudioSource>();
                    _src.spatialBlend = 0f;   // 2D: it's inside your head
                    _src.playOnAwake = false;
                    _src.priority = 32;
                }
                return _src != null && _clip != null;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        private static AudioClip Synthesize()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.42f);
            var data = new float[n];
            Thump(data, rate, 0.00f, 52f, 0.95f);   // lub
            Thump(data, rate, 0.17f, 44f, 0.70f);   // dub
            var clip = AudioClip.Create("advmedic_heartbeat", n, 1, rate, false);
            clip.SetData(new Il2CppStructArray<float>(data), 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }

        private static void Thump(float[] d, int rate, float start, float freq, float amp)
        {
            int s0 = (int)(start * rate);
            int len = (int)(0.16f * rate);
            for (int i = 0; i < len && s0 + i < d.Length; i++)
            {
                float t = i / (float)rate;
                float env = Mathf.Min(1f, t / 0.008f) * Mathf.Exp(-t * 28f);   // quick attack, fast decay
                float f = freq * (1f + 0.6f * Mathf.Exp(-t * 40f));              // slight pitch drop = "thud"
                d[s0 + i] += amp * env * Mathf.Sin(2f * Mathf.PI * f * t);
            }
        }

        private static void Fail(Exception e)
        {
            _failed = true;
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogWarning("[heartbeat] disabled: " + e.Message);
        }
    }
}
