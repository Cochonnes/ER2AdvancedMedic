using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Self-contained pain feedback (decision #6): a slow white flash pulse, plus an optional
    /// light camera shake that is auto-suppressed when ImmersiveEffects is installed so two shakes
    /// never stack. No cross-mod code.
    /// </summary>
    internal static class PainFX
    {
        private static Texture2D _vigNarrow, _vigWide;
        private static Texture2D Vignette(bool wide)
        {
            if (wide && _vigWide != null) return _vigWide;
            if (!wide && _vigNarrow != null) return _vigNarrow;

            const int N = 64;
            float inner = wide ? 0.35f : 0.62f;   // higher inner = band sits nearer the edges (~1/3 less inward)
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            float cx = (N - 1) * 0.5f, cy = (N - 1) * 0.5f, maxd = Mathf.Sqrt(cx * cx + cy * cy);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxd;
                    float a = Mathf.Clamp01((d - inner) / (1f - inner));
                    a = a * a;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply(); tex.wrapMode = TextureWrapMode.Clamp;
            if (wide) _vigWide = tex; else _vigNarrow = tex;
            return tex;
        }

        // Pulsing white vignette; scales with pain, and grows ~2x at high pain.
        public static void DrawFlash()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var c = Plugin.C;
            float intensity = Effects.PainIntensity;
            if (intensity <= 0.01f) return;

            float norm = Mathf.Clamp01(intensity / Mathf.Max(1f, c.HighIntensity.Value));
            float pulse = 0.55f + 0.45f * Mathf.Sin(Time.time * c.FlashSpeed.Value);
            // The configured peak opacity. (It used to be Mathf.Max(0.9, FlashStrength), which made every
            // value below 0.9 - the old 0.14 default included - draw at 0.9. The default is now that 0.9 and
            // an untouched old default is moved to it, so the out-of-box look is unchanged; see Cfg.)
            float strength = Mathf.Clamp01(c.FlashStrength.Value);
            var full = new Rect(0, 0, Screen.width, Screen.height);
            var prev = GUI.color;

            // Base narrow vignette.
            GUI.color = new Color(1f, 1f, 1f, norm * pulse * strength);
            GUI.DrawTexture(full, Vignette(false));

            // High pain (>50%) layers a wider vignette on top — roughly doubles the covered area.
            float wideMix = Mathf.Clamp01((norm - 0.5f) / 0.5f);
            if (wideMix > 0.01f)
            {
                GUI.color = new Color(1f, 1f, 1f, wideMix * pulse * strength);
                GUI.DrawTexture(full, Vignette(true));
            }
            GUI.color = prev;
        }

        /// <summary>
        /// Low-blood desaturation: the colour drains progressively from
        /// DesatFromBlood (class II, 85%) to DesatFullBlood (class IV, 60%). True desaturation needs a
        /// shader; this is an IMGUI approximation (a neutral-grey wash that drains apparent colour).
        /// </summary>
        public static void DrawDesaturation()
        {
            // Repaint only, before any lookup: OnGUI runs several passes a frame and only this one draws.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var p = Interop.Player();
            if (p == null) return;
            var w = WoundState.Peek(p);
            if (w == null || !w.VitalsInit) return;

            var c = Plugin.C;
            float t = Mathf.InverseLerp(c.DesatFromBlood.Value, c.DesatFullBlood.Value, w.Blood);
            float alpha = t * Mathf.Clamp01(c.DesatMaxAlpha.Value);
            if (alpha <= 0.01f) return;
            Gui.Rect(new Rect(0, 0, Screen.width, Screen.height), new Color(0.5f, 0.5f, 0.5f, alpha));
        }

        private static float _downSince = -1f, _nextDrift, _driftUntil;

        /// <summary>
        /// Unconscious screen for the local player while downed: fade to dark over ~2 s, then drift
        /// back toward consciousness for a few seconds every 15-20 s. Deeper in cardiac arrest. Kept
        /// lighter than a full blackout so a downed player can still watch for a medic.
        /// </summary>
        public static void DrawUnconscious()
        {
            // Repaint only (it runs once a frame, which is all the time-based drift below needs).
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var c = Plugin.C;
            var p = Interop.Player();
            bool downed = Interop.IsDowned(p);   // (downed reads IsAlive=false)
            if (!c.UnconsciousOverlay.Value || !downed) { _downSince = -1f; return; }

            float now = Time.time;
            if (_downSince < 0f) { _downSince = now; _nextDrift = now + UnityEngine.Random.Range(15f, 20f); }
            if (now >= _nextDrift) { _driftUntil = now + 3f; _nextDrift = now + UnityEngine.Random.Range(18f, 23f); }

            var w = WoundState.Peek(p);
            float target = w != null && w.Arrested ? c.ArrestDarkness.Value : c.UnconsciousDarkness.Value;
            float a = Mathf.Clamp01(target) * Mathf.Clamp01((now - _downSince) / 2f);
            if (now < _driftUntil) a *= 1f - 0.55f * Mathf.Sin((1f - (_driftUntil - now) / 3f) * Mathf.PI);   // lighten, then sink back
            if (a <= 0.01f) return;

            var full = new Rect(0, 0, Screen.width, Screen.height);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, a * 0.6f);
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = new Color(0f, 0f, 0f, a);
            GUI.DrawTexture(full, Vignette(true));   // heavier at the edges
            GUI.color = prev;
        }

        // Whether our self-shake is actually active this session.
        public static bool SelfShakeActive =>
            Plugin.C.SelfShake.Value && !Interop.ImmersiveShakePresent;

        private static Camera _cam;

        public static Camera Cam()
        {
            if (_cam != null) return _cam;
            try { _cam = Camera.main; } catch { }
            return _cam;
        }

        /// <summary>
        /// Optional self-contained pain shake. Runs from PlayerController.LateUpdate (after the
        /// game has posed the camera), so a per-frame rotational nudge is transient — the game
        /// recomputes camera rotation from input next frame, so nothing accumulates. Auto-off
        /// when ImmersiveEffects is installed.
        /// </summary>
        public static void Tick()
        {
            if (!SelfShakeActive) return;
            float intensity = Effects.PainIntensity;
            if (intensity <= 0.01f) return;

            var cam = Cam();
            if (cam == null) return;

            float norm = Mathf.Clamp01(intensity / Mathf.Max(1f, Plugin.C.HighIntensity.Value));
            float amt = norm * Plugin.C.SelfShakeScale.Value;   // degrees
            if (amt <= 0.001f) return;

            float rx = (Mathf.PerlinNoise(Time.time * 13f, 0f) - 0.5f) * 2f * amt;
            float ry = (Mathf.PerlinNoise(0f, Time.time * 11f) - 0.5f) * 2f * amt;
            try { cam.transform.Rotate(rx, ry, 0f, Space.Self); } catch { }
        }
    }
}
